using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BagaGame.Models;

namespace BagaGame.Services;

/// <summary>
/// Persists user edits (custom titles, hidden games, extra scan folders and
/// preferences) in <c>BagaGameData\library.json</c> next to the executable, or
/// in <c>%LocalAppData%\BagaGame</c> when that folder is read-only.
/// </summary>
public sealed class LibraryStore
{
    private readonly string _filePath;
    private readonly LibraryData _data;

    public LibraryStore()
        : this(AppPaths.DataDirectory)
    {
    }

    public LibraryStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "library.json");
        _data = Load(_filePath);
    }

    /// <summary>Folder that holds library.json, covers and the backups.</summary>
    public string DataDirectory => Path.GetDirectoryName(_filePath) ?? AppPaths.DataDirectory;

    /// <summary>User-picked covers live outside the movable download cache.</summary>
    public string CustomCoverDirectory => Path.Combine(DataDirectory, "covers-custom");

    public IReadOnlyList<string> Folders => _data.Folders;

    public IReadOnlyList<ManualGameEntry> ManualGames => _data.ManualGames;

    public int HiddenCount => _data.Hidden.Count;

    /// <summary>Ids of every hidden game, in the order they were hidden.</summary>
    public IReadOnlyList<string> Hidden => _data.Hidden;

    /// <summary>Last used library layout: grid (true) or list (false).</summary>
    public bool GridView
    {
        get => _data.GridView;
        set
        {
            if (_data.GridView == value)
            {
                return;
            }

            _data.GridView = value;
            Save();
        }
    }

    /// <summary>Last used grouping: None, Alphabet, Genre or Series.</summary>
    public string Grouping
    {
        get => string.IsNullOrWhiteSpace(_data.Grouping) ? "None" : _data.Grouping;
        set
        {
            if (string.Equals(_data.Grouping, value, StringComparison.Ordinal))
            {
                return;
            }

            _data.Grouping = value;
            Save();
        }
    }

    public bool DownloadCovers
    {
        get => _data.DownloadCovers;
        set
        {
            if (_data.DownloadCovers == value)
            {
                return;
            }

            _data.DownloadCovers = value;
            Save();
        }
    }

    /// <summary>Beta: use SteamGridDB instead of Steam for the cover picker.</summary>
    public bool UseSteamGridDb
    {
        get => _data.UseSteamGridDb;
        set
        {
            if (_data.UseSteamGridDb == value)
            {
                return;
            }

            _data.UseSteamGridDb = value;
            Save();
        }
    }

    public string SteamGridDbApiKey
    {
        get => _data.SteamGridDbApiKey ?? string.Empty;
        set
        {
            var key = value?.Trim() ?? string.Empty;
            if (string.Equals(_data.SteamGridDbApiKey, key, StringComparison.Ordinal))
            {
                return;
            }

            _data.SteamGridDbApiKey = key;
            Save();
        }
    }

    /// <summary>Light theme is opt-in; dark stays the default.</summary>
    public bool IsLightTheme
    {
        get => string.Equals(_data.Theme, "Light", StringComparison.OrdinalIgnoreCase);
        set
        {
            var theme = value ? "Light" : "Dark";
            if (string.Equals(_data.Theme, theme, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _data.Theme = theme;
            Save();
        }
    }

    /// <summary>
    /// Custom cover cache folder; <c>null</c> means the default
    /// %LocalAppData%\BagaGame\covers. An empty value resets the setting.
    /// </summary>
    public string? CoverCacheDirectory
    {
        get => string.IsNullOrWhiteSpace(_data.CacheDirectory) ? null : _data.CacheDirectory;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim();

            if (string.Equals(_data.CacheDirectory, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _data.CacheDirectory = normalized;
            Save();
        }
    }

    /// <summary>Interface language; <c>null</c> until the user picks one.</summary>
    public AppLanguage? Language
    {
        get => Enum.TryParse<AppLanguage>(_data.Language, ignoreCase: true, out var language)
            ? language
            : null;
        set
        {
            var stored = value?.ToString();

            if (string.Equals(_data.Language, stored, StringComparison.Ordinal))
            {
                return;
            }

            _data.Language = stored;
            Save();
        }
    }

    /// <summary>
    /// Canonical Steam genre remembered for a game (for example "Action"), if
    /// any. Older builds stored the Russian label; it is mapped back on read.
    /// </summary>
    public string? GetGenre(string gameId)
        => _data.Genres.TryGetValue(gameId, out var genre) && !string.IsNullOrWhiteSpace(genre)
            ? Loc.NormalizeStoredGenre(genre)
            : null;

    /// <summary>Seconds played, remembered across launches. 0 when unknown.</summary>
    public long GetPlaytime(string gameId)
        => _data.Playtime.TryGetValue(gameId, out var seconds) && seconds > 0 ? seconds : 0;

    public void SetPlaytime(string gameId, long seconds)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return;
        }

        var normalized = seconds < 0 ? 0 : seconds;
        if (_data.Playtime.TryGetValue(gameId, out var existing) && existing == normalized)
        {
            return;
        }

        if (normalized == 0)
        {
            _data.Playtime.Remove(gameId);
        }
        else
        {
            _data.Playtime[gameId] = normalized;
        }

        Save();
    }

    public void SetGenre(string gameId, string genre)
    {
        if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(genre))
        {
            return;
        }

        if (_data.Genres.TryGetValue(gameId, out var existing) &&
            string.Equals(existing, genre, StringComparison.Ordinal))
        {
            return;
        }

        _data.Genres[gameId] = genre;
        Save();
    }

    public bool IsHidden(string gameId)
        => _data.Hidden.Contains(gameId, StringComparer.OrdinalIgnoreCase);

    /// <summary>Human-readable title remembered when the game was hidden.</summary>
    public string? GetHiddenTitle(string gameId)
        => _data.HiddenTitles.TryGetValue(gameId, out var title) &&
           !string.IsNullOrWhiteSpace(title)
            ? title
            : null;

    /// <summary>Custom title set by the user, if any.</summary>
    public string? GetCustomTitle(string gameId)
        => _data.CustomTitles.TryGetValue(gameId, out var title) &&
           !string.IsNullOrWhiteSpace(title)
            ? title
            : null;

    public void Unhide(string gameId)
    {
        var removed = _data.Hidden.RemoveAll(
            item => string.Equals(item, gameId, StringComparison.OrdinalIgnoreCase));

        foreach (var key in _data.HiddenTitles.Keys
                     .Where(key => string.Equals(key, gameId, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            removed += _data.HiddenTitles.Remove(key) ? 1 : 0;
        }

        if (removed > 0)
        {
            Save();
        }
    }

    /// <summary>Adds (or refreshes) a manually added game.</summary>
    public void AddManualGame(ManualGameEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id) ||
            string.IsNullOrWhiteSpace(entry.InstallPath) ||
            string.IsNullOrWhiteSpace(entry.ExecutablePath))
        {
            return;
        }

        var index = _data.ManualGames.FindIndex(
            item => string.Equals(item.Id, entry.Id, StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
        {
            _data.ManualGames[index] = entry;
        }
        else
        {
            _data.ManualGames.Add(entry);
        }

        Unhide(entry.Id);
        Save();
    }

    /// <summary>
    /// Rewrites an existing manual entry (for example when its title and
    /// install folder were repaired) without unhiding it.
    /// </summary>
    public void UpdateManualGame(ManualGameEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id) ||
            string.IsNullOrWhiteSpace(entry.InstallPath) ||
            string.IsNullOrWhiteSpace(entry.ExecutablePath))
        {
            return;
        }

        var index = _data.ManualGames.FindIndex(
            item => string.Equals(item.Id, entry.Id, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            AddManualGame(entry);
            return;
        }

        _data.ManualGames[index] = entry;
        Save();
    }

    /// <summary>Removes a manually added game from the persisted list.</summary>
    public void RemoveManualGame(string gameId)
    {
        if (_data.ManualGames.RemoveAll(
                item => string.Equals(item.Id, gameId, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            Save();
        }
    }

    /// <summary>Absolute path of the user's cover for a game, if it still exists.</summary>
    public string? GetCustomCoverPath(string gameId)
    {
        if (!_data.CustomCovers.TryGetValue(gameId, out var fileName) ||
            string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        try
        {
            var path = Path.Combine(CustomCoverDirectory, fileName);
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Stores an already normalized 600x900 JPEG and remembers it for the game.
    /// Returns the path so the caller can update the card immediately.
    /// </summary>
    public string? SaveCustomCover(string gameId, byte[] jpeg)
    {
        if (string.IsNullOrWhiteSpace(gameId) || jpeg.Length < 512)
        {
            return null;
        }

        var tempPath = string.Empty;

        try
        {
            Directory.CreateDirectory(CustomCoverDirectory);

            var fileName = CustomCoverFileName(gameId);
            var path = Path.Combine(CustomCoverDirectory, fileName);
            tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

            File.WriteAllBytes(tempPath, jpeg);

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null);
            }
            else
            {
                File.Move(tempPath, path);
            }

            _data.CustomCovers[gameId] = fileName;
            Save();
            return path;
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
            Trace.TraceError($"BagaGame: could not save custom cover: {ex.Message}");
            return null;
        }
        finally
        {
            try
            {
                if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Best effort; the temporary file has a unique name.
            }
        }
    }

    /// <summary>Drops the user's cover and lets the downloaded cover return.</summary>
    public void RemoveCustomCover(string gameId)
    {
        if (!_data.CustomCovers.TryGetValue(gameId, out var fileName))
        {
            return;
        }

        _data.CustomCovers.Remove(gameId);
        TryDeleteCustomCover(fileName);
        Save();
    }

    private void TryDeleteCustomCover(string fileName)
    {
        try
        {
            var path = Path.Combine(CustomCoverDirectory, fileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A leftover image is harmless.
        }
    }

    private static string CustomCoverFileName(string gameId)
    {
        using var sha = SHA1.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(gameId));
        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant() + ".jpg";
    }

    /// <summary>Applies saved renames and drops games the user deleted.</summary>
    public List<Game> Apply(IEnumerable<Game> games)
    {
        var incoming = games.ToList();
        var result = new List<Game>();

        // A hidden "manual:" id only makes sense while its entry exists; the
        // entry is removed together with the game, so such leftovers would
        // inflate "Скрыто игр" forever.
        var pruned = _data.Hidden.RemoveAll(
            id => id.StartsWith("manual:", StringComparison.OrdinalIgnoreCase) &&
                  !incoming.Any(game => string.Equals(game.Id, id, StringComparison.OrdinalIgnoreCase)));

        if (pruned > 0)
        {
            Save();
        }

        var staleTitles = 0;

        foreach (var key in _data.HiddenTitles.Keys
                     .Where(key => !_data.Hidden.Contains(key, StringComparer.OrdinalIgnoreCase))
                     .ToList())
        {
            staleTitles += _data.HiddenTitles.Remove(key) ? 1 : 0;
        }

        if (staleTitles > 0)
        {
            Save();
        }

        var hiddenTitlesChanged = false;

        foreach (var game in incoming)
        {
            // Apply the rename before the hidden check, so the list of hidden
            // games shows the name the user actually gave the card.
            if (_data.CustomTitles.TryGetValue(game.Id, out var title) &&
                !string.IsNullOrWhiteSpace(title))
            {
                game.Title = title;
            }

            if (_data.Hidden.Contains(game.Id, StringComparer.OrdinalIgnoreCase))
            {
                // Remember the title of games hidden by older builds, which
                // did not store one, so Settings can list them by name.
                if (!_data.HiddenTitles.TryGetValue(game.Id, out var hiddenTitle) ||
                    !string.Equals(hiddenTitle, game.Title, StringComparison.Ordinal))
                {
                    _data.HiddenTitles[game.Id] = game.Title;
                    hiddenTitlesChanged = true;
                }

                continue;
            }

            result.Add(game);
        }

        if (hiddenTitlesChanged)
        {
            Save();
        }

        return result;
    }

    public void Rename(string gameId, string title)
    {
        _data.CustomTitles[gameId] = title;
        Save();
    }

    public void Hide(string gameId, string? title = null)
    {
        var changed = false;

        if (!_data.Hidden.Contains(gameId, StringComparer.OrdinalIgnoreCase))
        {
            _data.Hidden.Add(gameId);
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            if (!_data.HiddenTitles.TryGetValue(gameId, out var stored) ||
                !string.Equals(stored, title, StringComparison.Ordinal))
            {
                _data.HiddenTitles[gameId] = title;
                changed = true;
            }
        }

        // Hidden manual entries stay in ManualGames: "Hide" only removes the
        // card, while "Delete" calls RemoveManualGame explicitly. Otherwise a
        // hidden manual game could never be restored.

        if (changed)
        {
            Save();
        }
    }

    public void RestoreHidden()
    {
        if (_data.Hidden.Count == 0)
        {
            return;
        }

        _data.Hidden.Clear();
        _data.HiddenTitles.Clear();
        Save();
    }

    /// <summary>
    /// Drops the remembered rename and genre of one game, so the next scan
    /// rebuilds the card from scratch ("Скрыть и удалить").
    /// </summary>
    public void ForgetGame(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return;
        }

        var changed = false;

        foreach (var key in _data.CustomTitles.Keys
                     .Where(key => string.Equals(key, gameId, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            changed |= _data.CustomTitles.Remove(key);
        }

        foreach (var key in _data.Genres.Keys
                     .Where(key => string.Equals(key, gameId, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            changed |= _data.Genres.Remove(key);
        }

        foreach (var key in _data.Playtime.Keys
                     .Where(key => string.Equals(key, gameId, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            changed |= _data.Playtime.Remove(key);
        }

        if (_data.CustomCovers.TryGetValue(gameId, out var coverName))
        {
            _data.CustomCovers.Remove(gameId);
            TryDeleteCustomCover(coverName);
            changed = true;
        }

        if (changed)
        {
            Save();
        }
    }

    public void AddFolder(string path)
    {
        var normalized = NormalizeFolder(path);
        if (normalized is null)
        {
            return;
        }

        if (_data.Folders.Any(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _data.Folders.Add(normalized);
        Save();
    }

    public void RemoveFolder(string path)
    {
        var index = _data.Folders.FindIndex(
            item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            return;
        }

        _data.Folders.RemoveAt(index);
        Save();
    }

    private static string? NormalizeFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(path.Trim());
            return Directory.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    private static LibraryData Load(string path)
        => TryLoad(path) ?? TryLoad(path + ".bak") ?? new LibraryData();

    private static LibraryData? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or JsonException)
        {
            // A broken settings file must never block the app. The previous
            // successful save is available in library.json.bak.
            Trace.TraceWarning($"BagaGame: could not load settings from '{path}': {ex.Message}");
            return null;
        }
    }

    private void Save()
    {
        // A unique name per save: two windows (or a crash during a save) can
        // never write into the same temporary file.
        var tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            var json = JsonSerializer.Serialize(
                _data,
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(tempPath, json);

            if (File.Exists(_filePath))
            {
                // Atomic replace that keeps the last valid version around.
                File.Replace(tempPath, _filePath, _filePath + ".bak");
            }
            else
            {
                File.Move(tempPath, _filePath);
            }
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
            // Keep the current session alive, but leave a trace instead of
            // silently pretending that the settings were persisted.
            Trace.TraceError($"BagaGame: could not save settings to '{_filePath}': {ex}");
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // A leftover uniquely named temp file cannot corrupt the store.
            }
        }
    }

    private sealed class LibraryData
    {
        public List<string> Hidden { get; set; } = new();

        /// <summary>Titles of hidden games, so Settings can list them by name.</summary>
        public Dictionary<string, string> HiddenTitles { get; set; } = new();

        /// <summary>Game id to file name in <c>covers-custom</c>.</summary>
        public Dictionary<string, string> CustomCovers { get; set; } = new();

        public Dictionary<string, string> CustomTitles { get; set; } = new();

        public List<string> Folders { get; set; } = new();

        public List<ManualGameEntry> ManualGames { get; set; } = new();

        public Dictionary<string, string> Genres { get; set; } = new();

        public Dictionary<string, long> Playtime { get; set; } = new();

        public bool DownloadCovers { get; set; } = true;

        public bool UseSteamGridDb { get; set; }

        public string? SteamGridDbApiKey { get; set; }

        public string Theme { get; set; } = "Dark";

        public string? Language { get; set; }

        public string? CacheDirectory { get; set; }

        public bool GridView { get; set; } = true;

        public string Grouping { get; set; } = "None";
    }
}
