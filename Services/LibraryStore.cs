using System.IO;
using System.Text.Json;
using BagaGame.Models;

namespace BagaGame.Services;

/// <summary>
/// Persists user edits (custom titles, hidden games, extra scan folders and
/// preferences) in %LocalAppData%\BagaGame\library.json.
/// </summary>
public sealed class LibraryStore
{
    private readonly string _filePath;
    private readonly LibraryData _data;

    public LibraryStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BagaGame");

        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "library.json");
        _data = Load(_filePath);
    }

    public IReadOnlyList<string> Folders => _data.Folders;

    public IReadOnlyList<ManualGameEntry> ManualGames => _data.ManualGames;

    public int HiddenCount => _data.Hidden.Count;

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

    /// <summary>Steam genre remembered for a game (Russian label), if any.</summary>
    public string? GetGenre(string gameId)
        => _data.Genres.TryGetValue(gameId, out var genre) && !string.IsNullOrWhiteSpace(genre)
            ? genre
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

    public void Unhide(string gameId)
    {
        var removed = _data.Hidden.RemoveAll(
            item => string.Equals(item, gameId, StringComparison.OrdinalIgnoreCase));

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

        foreach (var game in incoming)
        {
            if (_data.Hidden.Contains(game.Id, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_data.CustomTitles.TryGetValue(game.Id, out var title) &&
                !string.IsNullOrWhiteSpace(title))
            {
                game.Title = title;
            }

            result.Add(game);
        }

        return result;
    }

    public void Rename(string gameId, string title)
    {
        _data.CustomTitles[gameId] = title;
        Save();
    }

    public void Hide(string gameId)
    {
        var changed = false;

        if (!_data.Hidden.Contains(gameId, StringComparer.OrdinalIgnoreCase))
        {
            _data.Hidden.Add(gameId);
            changed = true;
        }

        // A manually added game is removed from the persisted list too;
        // otherwise "restore hidden" would bring a deleted game back.
        if (_data.ManualGames.RemoveAll(
                item => string.Equals(item.Id, gameId, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            changed = true;
        }

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
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize<LibraryData>(json);
                if (data is not null)
                {
                    return data;
                }
            }
        }
        catch
        {
            // A broken settings file must never block the app.
        }

        return new LibraryData();
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(
                _data,
                new JsonSerializerOptions { WriteIndented = true });

            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch
        {
            // Preferences are best-effort; the current session keeps working.
        }
    }

    private sealed class LibraryData
    {
        public List<string> Hidden { get; set; } = new();

        public Dictionary<string, string> CustomTitles { get; set; } = new();

        public List<string> Folders { get; set; } = new();

        public List<ManualGameEntry> ManualGames { get; set; } = new();

        public Dictionary<string, string> Genres { get; set; } = new();

        public Dictionary<string, long> Playtime { get; set; } = new();

        public bool DownloadCovers { get; set; } = true;

        public string Theme { get; set; } = "Dark";

        public string? CacheDirectory { get; set; }

        public bool GridView { get; set; } = true;

        public string Grouping { get; set; } = "None";
    }
}
