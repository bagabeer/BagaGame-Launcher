using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BagaGame.Models;

namespace BagaGame.Services;

/// <summary>Ubisoft Uplay R1/R2 emulator layout for one game.</summary>
public sealed record UplayEmulator(
    string UplayId,
    string SaveDirectory,
    string ConfigPath);

/// <summary>Nemirtingas Epic Games Emulator layout for one game.</summary>
public sealed record EpicEmulator(
    string EpicAppId,
    string SaveDirectory,
    string ConfigPath);

/// <summary>
/// Achievement support for the non-Steam platforms used by repacks:
/// Nemirtingas Epic Games Emulator and the Uplay R1/R2 emulators. The Steam
/// emulators keep their own service; this one only handles the platform
/// specific save files and the Uplay achievement schema.
/// </summary>
public static class PlatformAchievementService
{
    private const int MaxParentLevels = 8;

    private static readonly HttpClient Icons = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Runs the local, non-network part of the setup after a scan.</summary>
    public static void Prepare(IEnumerable<Game> games, CancellationToken cancellationToken)
    {
        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareGame(game);
        }
    }

    /// <summary>
    /// Enables achievement tracking in the Uplay R2 ini and makes sure its save
    /// folder exists. Safe to call repeatedly.
    /// </summary>
    public static void PrepareGame(Game game)
    {
        if (game is null ||
            (game.Source != GameLibraryService.FolderSource && !game.IsManual))
        {
            return;
        }

        try
        {
            var uplay = DetectUplay(game);
            if (uplay is not null)
            {
                EnableUplayAchievements(uplay.ConfigPath);
                Directory.CreateDirectory(uplay.SaveDirectory);
            }

            PrepareEpic(game);
        }
        catch
        {
            // A locked or read-only game folder only skips the setup.
        }
    }

    /// <summary>
    /// Creates the Nemirtingas save folder when the config already knows the
    /// generated Epic user id. The emulator writes achievements.json there on
    /// its first run.
    /// </summary>
    private static void PrepareEpic(Game game)
    {
        var config = FindEpicConfig(game);
        if (config is null)
        {
            return;
        }

        var appId = ReadJsonString(config, "appid") ??
                    ReadJsonPathString(config, "EOSEmu", "Application", "AppId");
        var epicId = ReadJsonString(config, "epicid") ??
                     ReadJsonPathString(config, "EOSEmu", "User", "EpicId");
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(epicId))
        {
            return;
        }

        var savePath = ReadJsonString(config, "savepath") ??
                       ReadJsonPathString(config, "EOSEmu", "Application", "SavePath") ??
                       "appdata";
        var root = GetEpicSaveRoot(game.InstallPath ?? string.Empty, savePath);
        var saveDirectory = Path.Combine(root, epicId!, appId!);

        Directory.CreateDirectory(saveDirectory);
        EnsureJsonArray(Path.Combine(saveDirectory, "achievements.json"));
        EnsureJsonArray(Path.Combine(saveDirectory, "stats.json"));
    }

    private static void EnsureJsonArray(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path, "[]", new UTF8Encoding(false));
            }
        }
        catch
        {
            // The emulator creates the file itself on first use.
        }
    }

    /// <summary>Adds local Ubisoft and Epic unlocks to the Steam/local merge.</summary>
    public static void Read(Game game, LocalAchievements result)
    {
        ReadUplay(game, result);
        ReadEpic(game, result);
    }

    /// <summary>
    /// Downloads the Steam achievement list and writes the Uplay R2 schema,
    /// state file and local images. The Uplay emulator reads these from its
    /// save folder, not from the Steam emulator directories.
    /// </summary>
    public static async Task ProvisionSchemaAsync(
        Game game,
        int steamAppId,
        SteamAchievementsService steam,
        CancellationToken cancellationToken)
    {
        if (game is null || steamAppId <= 0)
        {
            return;
        }

        var uplay = DetectUplay(game);
        if (uplay is null)
        {
            return;
        }

        var schemaPath = Path.Combine(uplay.SaveDirectory, "achievements_schema.json");
        var statePath = Path.Combine(uplay.SaveDirectory, "achievements.json");

        try
        {
            if (File.Exists(schemaPath) && new FileInfo(schemaPath).Length > 2)
            {
                if (!NeedsUplayMigration(schemaPath, statePath))
                {
                    var configDirectory = Path.GetDirectoryName(uplay.ConfigPath);
                    if (!string.IsNullOrWhiteSpace(configDirectory))
                    {
                        CopyIcons(
                            Path.Combine(uplay.SaveDirectory, "achievements"),
                            Path.Combine(configDirectory!, "achievements"));

                        CopyFileIfMissing(
                            schemaPath,
                            Path.Combine(configDirectory!, "achievements_schema.json"));
                        CopyFileIfMissing(
                            statePath,
                            Path.Combine(configDirectory!, "achievements.json"));
                    }

                    return;
                }

                // The game wrote numeric Uplay ids while the schema still
                // uses Steam API names. Rebuild both files with numeric keys
                // and keep every earned entry.
            }
        }
        catch
        {
            return;
        }

        try
        {
            var result = await steam
                .GetAsync(steamAppId, cancellationToken)
                .ConfigureAwait(false);

            if (result.Status != SteamAchievementsStatus.Success)
            {
                return;
            }

            var achievements = result.Achievements
                .Where(item => !string.IsNullOrWhiteSpace(item.ApiName))
                .ToList();

            if (achievements.Count == 0)
            {
                return;
            }

            var images = Path.Combine(uplay.SaveDirectory, "achievements");
            Directory.CreateDirectory(images);

            var configDirectory = Path.GetDirectoryName(uplay.ConfigPath);
            var statePaths = new List<string> { statePath };
            if (!string.IsNullOrWhiteSpace(configDirectory))
            {
                statePaths.Add(Path.Combine(configDirectory!, "achievements.json"));
            }

            var earned = ReadUplayEarned(statePaths);
            var schema = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var state = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            foreach (var achievement in achievements)
            {
                var file = SafeFileName(achievement.ApiName);
                var key = GetUplayKey(achievement.ApiName);
                var displayName = string.IsNullOrWhiteSpace(achievement.Name)
                    ? achievement.ApiName
                    : achievement.Name;

                var isEarned = false;
                var earnedTime = 0L;

                if (earned.TryGetValue(key, out var saved) ||
                    earned.TryGetValue(achievement.ApiName, out saved))
                {
                    isEarned = saved.Earned;
                    earnedTime = saved.Time;
                }

                schema[key] = new
                {
                    displayName,
                    description = achievement.Description ?? string.Empty,
                    earned = false,
                    earned_time = 0,
                    icon = "achievements/" + file + ".jpg",
                    icon_gray = "achievements/" + file + "_gray.jpg",
                };

                state[key] = new
                {
                    displayName,
                    description = achievement.Description ?? string.Empty,
                    earned = isEarned,
                    earned_time = earnedTime,
                    icon = "achievements/" + file + ".jpg",
                    icon_gray = "achievements/" + file + "_gray.jpg",
                };
            }

            var schemaJson = JsonSerializer.Serialize(
                schema,
                new JsonSerializerOptions { WriteIndented = true });
            var stateJson = JsonSerializer.Serialize(
                state,
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(schemaPath, schemaJson, new UTF8Encoding(false));
            File.WriteAllText(statePath, stateJson, new UTF8Encoding(false));

            if (!string.IsNullOrWhiteSpace(configDirectory))
            {
                // Migration must replace the old API-name copy, so this one
                // writes instead of only filling a missing file.
                File.WriteAllText(
                    Path.Combine(configDirectory!, "achievements_schema.json"),
                    schemaJson,
                    new UTF8Encoding(false));
                File.WriteAllText(
                    Path.Combine(configDirectory!, "achievements.json"),
                    stateJson,
                    new UTF8Encoding(false));
            }

            using var gate = new SemaphoreSlim(6);
            await Task.WhenAll(achievements.Select(async achievement =>
            {
                var file = SafeFileName(achievement.ApiName);

                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await DownloadIconAsync(
                            achievement.IconOpen,
                            Path.Combine(images, file + ".jpg"),
                            cancellationToken)
                        .ConfigureAwait(false);

                    await DownloadIconAsync(
                            achievement.IconLocked,
                            Path.Combine(images, file + "_gray.jpg"),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);

            // Some voices38 builds look for the images beside the game
            // executable; the schema and state were already written there.
            if (!string.IsNullOrWhiteSpace(configDirectory))
            {
                CopyIcons(images, Path.Combine(configDirectory!, "achievements"));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Offline or a private Steam profile: the local saves still work.
        }
    }

    // ---------------------------------------------------------------------
    // Ubisoft / Uplay R1 + R2
    // ---------------------------------------------------------------------

    private sealed class UplayStateEntry
    {
        public bool Earned { get; init; }

        public long Time { get; init; }
    }

    /// <summary>
    /// The voices38 loader writes numeric Uplay achievement ids, while the
    /// Steam schema uses names such as "acobsidian_ach_18". A state file with
    /// numeric keys and a schema without them means the two are out of sync.
    /// </summary>
    private static bool NeedsUplayMigration(string schemaPath, string statePath)
        => ReadJsonKeys(statePath).Any(IsNumeric) &&
           !ReadJsonKeys(schemaPath).Any(IsNumeric);

    private static List<string> ReadJsonKeys(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject().Select(item => item.Name).ToList()
                : new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static Dictionary<string, UplayStateEntry> ReadUplayEarned(
        IEnumerable<string> paths)
    {
        var earned = new Dictionary<string, UplayStateEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    var value = property.Value;
                    var isEarned = false;
                    var time = 0L;

                    if (value.ValueKind == JsonValueKind.Object)
                    {
                        isEarned = GetBool(value, "earned") ||
                                   GetLong(value, "earned_time") > 0 ||
                                   GetLong(value, "unlock_time") > 0;

                        time = Math.Max(time, GetLong(value, "earned_time"));
                        time = Math.Max(time, GetLong(value, "unlock_time"));
                    }
                    else if (value.ValueKind == JsonValueKind.True)
                    {
                        isEarned = true;
                    }

                    if (isEarned)
                    {
                        earned[property.Name] = new UplayStateEntry
                        {
                            Earned = true,
                            Time = time,
                        };
                    }
                }
            }
            catch
            {
                // One malformed copy must not hide the other one.
            }
        }

        return earned;
    }

    private static string GetUplayKey(string apiName)
    {
        var separator = apiName.LastIndexOf('_');
        if (separator >= 0 && separator < apiName.Length - 1)
        {
            var suffix = apiName.Substring(separator + 1);
            if (IsNumeric(suffix))
            {
                return suffix;
            }
        }

        return apiName;
    }

    private static bool IsNumeric(string value)
        => value.Length > 0 && value.All(char.IsDigit);

    public static UplayEmulator? DetectUplay(Game game)
    {
        var config = FindUplayConfig(game);
        if (config is null)
        {
            return null;
        }

        var install = game.InstallPath ?? string.Empty;
        var saveRoot = GetUplaySaveRoot(install, config);
        if (string.IsNullOrWhiteSpace(saveRoot) || !Directory.Exists(saveRoot))
        {
            return null;
        }

        var uplayId = FindUplayId(game, saveRoot, config);
        if (string.IsNullOrWhiteSpace(uplayId))
        {
            return null;
        }

        return new UplayEmulator(
            uplayId,
            Path.Combine(saveRoot, uplayId),
            config);
    }

    private static string? FindUplayConfig(Game game)
    {
        foreach (var directory in GetSearchDirectories(game))
        {
            try
            {
                foreach (var name in new[] { "upc_r2.ini", "uplay_r2.ini", "upc_r1.ini", "uplay_r1.ini" })
                {
                    var path = Path.Combine(directory, name);
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
            }
            catch
            {
                // One unreadable folder must not stop the other candidates.
            }
        }

        return null;
    }

    private static string GetUplaySaveRoot(string install, string config)
    {
        var saveType = ReadIniValue(config, "SaveType") ?? "0";
        var savePath = ReadIniValue(config, "SavePath") ?? string.Empty;

        if (saveType is "1" or "2" && !string.IsNullOrWhiteSpace(savePath))
        {
            return Path.IsPathRooted(savePath)
                ? savePath
                : Path.Combine(install, savePath);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            IsUplayR1(config) ? "R1 UplayEmu Saves" : "Goldberg UplayEmu Saves");
    }

    private static bool IsUplayR1(string config)
    {
        var name = Path.GetFileName(config);
        return name.StartsWith("upc_r1", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("uplay_r1", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The emulator gets the Uplay product id from UPC_Init, so it is not in
    /// the ini. The save folder is the id, and its save files carry the game's
    /// exe prefix ("ACBlackFlag[AutoSave01].save").
    /// </summary>
    private static string? FindUplayId(Game game, string saveRoot, string config)
    {
        var exeStem = string.Empty;
        try
        {
            exeStem = Path.GetFileNameWithoutExtension(game.ExecutablePath) ?? string.Empty;
        }
        catch
        {
            // The heading below still uses the title.
        }

        var title = game.Title ?? string.Empty;

        try
        {
            var folders = Directory.GetDirectories(saveRoot)
                .Where(folder => !string.Equals(
                    Path.GetFileName(folder),
                    "settings",
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var folder in folders)
            {
                var id = Path.GetFileName(folder);

                if (ContainsKnownUplayGame(id, title, exeStem))
                {
                    return id;
                }

                foreach (var save in Directory.EnumerateFiles(folder, "*.save"))
                {
                    var name = Path.GetFileNameWithoutExtension(save);
                    if (exeStem.Length >= 4 &&
                        name.StartsWith(exeStem, StringComparison.OrdinalIgnoreCase))
                    {
                        return id;
                    }

                    if (title.Length >= 4 &&
                        name.IndexOf(title.Split(' ')[0], StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return id;
                    }
                }
            }

            // A single save folder is unambiguous even before the first save.
            return folders.Count == 1 ? Path.GetFileName(folders[0]) : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool ContainsKnownUplayGame(string uplayId, string title, string exeStem)
    {
        if (uplayId == "66088" &&
            (title.IndexOf("Black Flag", StringComparison.OrdinalIgnoreCase) >= 0 ||
             exeStem.IndexOf("BlackFlag", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return true;
        }

        return false;
    }

    /// <summary>Sets Achievements=1 in the existing [Settings] section.</summary>
    private static void EnableUplayAchievements(string configPath)
    {
        try
        {
            var lines = File.ReadAllLines(configPath).ToList();
            var settings = lines.FindIndex(line =>
                string.Equals(line.Trim(), "[Settings]", StringComparison.OrdinalIgnoreCase));

            if (settings < 0)
            {
                lines.Add("[Settings]");
                lines.Add("Achievements = 1");
                File.WriteAllLines(configPath, lines, new UTF8Encoding(false));
                return;
            }

            var end = lines.Count;
            for (var index = settings + 1; index < lines.Count; index++)
            {
                if (lines[index].TrimStart().StartsWith("[", StringComparison.Ordinal))
                {
                    end = index;
                    break;
                }
            }

            for (var index = settings + 1; index < end; index++)
            {
                var line = lines[index];
                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                if (!line.Substring(0, separator).Trim()
                        .Equals("Achievements", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (line.Substring(separator + 1).Trim() != "1")
                {
                    lines[index] = "Achievements = 1";
                    File.WriteAllLines(configPath, lines, new UTF8Encoding(false));
                }

                return;
            }

            lines.Insert(end, "Achievements = 1");
            File.WriteAllLines(configPath, lines, new UTF8Encoding(false));
        }
        catch
        {
            // A read-only game folder keeps the emulator's own setting.
        }
    }

    private static void ReadUplay(Game game, LocalAchievements result)
    {
        try
        {
            var uplay = DetectUplay(game);
            if (uplay is null)
            {
                return;
            }

            var paths = new List<string>
            {
                Path.Combine(uplay.SaveDirectory, "achievements.json"),
            };

            var configDirectory = Path.GetDirectoryName(uplay.ConfigPath);
            if (!string.IsNullOrWhiteSpace(configDirectory))
            {
                paths.Add(Path.Combine(configDirectory!, "achievements.json"));
            }

            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(path))
                {
                    ReadUplayFile(path, result);
                }
            }
        }
        catch
        {
            // The emulator can be writing the file while the game runs.
        }
    }

    private static void ReadUplayFile(string path, LocalAchievements result)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                {
                    ReadUplayEntry(property.Name, property.Value, result);
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var id = GetString(item, "achievement_id") ??
                             GetString(item, "id") ??
                             GetString(item, "name");

                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        ReadUplayEntry(id, item, result);
                    }
                }
            }
        }
        catch
        {
            // One malformed copy must not hide the other one.
        }
    }

    private static void ReadUplayEntry(
        string id,
        JsonElement value,
        LocalAchievements result)
    {
        var earned = false;
        string? display = null;
        var timestamp = 0L;

        if (value.ValueKind == JsonValueKind.Object)
        {
            timestamp = Math.Max(timestamp, GetLong(value, "earned_time"));
            timestamp = Math.Max(timestamp, GetLong(value, "unlock_time"));

            earned = GetBool(value, "earned") || timestamp > 0;

            display = GetString(value, "displayName") ??
                      GetString(value, "display_name") ??
                      GetString(value, "name");
        }
        else if (value.ValueKind == JsonValueKind.True)
        {
            earned = true;
        }

        if (!earned)
        {
            return;
        }

        result.ApiNames.Add(id);

        if (timestamp > 0)
        {
            result.AddUnlockTime(id, timestamp);
        }

        if (!string.IsNullOrWhiteSpace(display))
        {
            result.DisplayNames.Add(display!);

            if (timestamp > 0)
            {
                result.AddUnlockTime(display, timestamp);
            }
        }

        result.Sources.Add("Ubisoft");
    }

    // ---------------------------------------------------------------------
    // Epic / Nemirtingas Epic Games Emulator
    // ---------------------------------------------------------------------

    public static EpicEmulator? DetectEpic(Game game)
    {
        var config = FindEpicConfig(game);
        if (config is null)
        {
            return null;
        }

        var epicAppId = ReadJsonString(config, "appid") ??
                        ReadJsonPathString(config, "EOSEmu", "Application", "AppId");
        if (string.IsNullOrWhiteSpace(epicAppId))
        {
            return null;
        }

        var savePath = ReadJsonString(config, "savepath") ??
                       ReadJsonPathString(config, "EOSEmu", "Application", "SavePath") ??
                       "appdata";
        var saveRoot = GetEpicSaveRoot(game.InstallPath ?? string.Empty, savePath);

        var directory = FindEpicSaveDirectory(saveRoot, epicAppId);
        if (directory is null)
        {
            return null;
        }

        return new EpicEmulator(epicAppId, directory, config);
    }

    private static string GetEpicSaveRoot(string install, string? savePath)
        => string.Equals(savePath, "appdata", StringComparison.OrdinalIgnoreCase) ||
           string.IsNullOrWhiteSpace(savePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NemirtingasEpicEmu")
            : Path.IsPathRooted(savePath)
                ? Path.Combine(savePath, "NemirtingasEpicEmu")
                : Path.Combine(install, savePath, "NemirtingasEpicEmu");

    private static string? FindEpicConfig(Game game)
    {
        foreach (var directory in GetSearchDirectories(game))
        {
            try
            {
                var nested = Path.Combine(directory, "nepice_settings", "NemirtingasEpicEmu.json");
                if (File.Exists(nested))
                {
                    return nested;
                }

                var direct = Path.Combine(directory, "NemirtingasEpicEmu.json");
                if (File.Exists(direct))
                {
                    return direct;
                }
            }
            catch
            {
                // Continue with the next candidate.
            }
        }

        return null;
    }

    private static string? FindEpicSaveDirectory(string saveRoot, string epicAppId)
    {
        try
        {
            if (!Directory.Exists(saveRoot))
            {
                return null;
            }

            // <saveRoot>\<epicid>\<appid>
            var direct = Path.Combine(saveRoot, epicAppId);
            if (Directory.Exists(direct))
            {
                return direct;
            }

            foreach (var user in Directory.GetDirectories(saveRoot))
            {
                var candidate = Path.Combine(user, epicAppId);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // A missing or locked save tree simply has no Epic data.
        }

        return null;
    }

    private static void ReadEpic(Game game, LocalAchievements result)
    {
        try
        {
            var epic = DetectEpic(game);
            if (epic is null)
            {
                return;
            }

            var path = Path.Combine(epic.SaveDirectory, "achievements.json");
            if (!File.Exists(path))
            {
                return;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                {
                    ReadEpicEntry(property.Name, property.Value, result);
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var id = GetString(item, "achievement_id") ??
                             GetString(item, "AchievementId") ??
                             GetString(item, "id") ??
                             GetString(item, "Name") ??
                             GetString(item, "name");

                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        ReadEpicEntry(id, item, result);
                    }
                }
            }
        }
        catch
        {
            // The emulator can flush the file at exit.
        }
    }

    private static void ReadEpicEntry(
        string id,
        JsonElement value,
        LocalAchievements result)
    {
        var timestamp = Math.Max(GetLong(value, "earned_time"), GetLong(value, "unlock_time"));
        timestamp = Math.Max(timestamp, GetLong(value, "UnlockTime"));

        var earned = GetBool(value, "earned") ||
                     GetBool(value, "Earned") ||
                     timestamp > 0;

        if (!earned)
        {
            return;
        }

        result.ApiNames.Add(id);

        if (timestamp > 0)
        {
            result.AddUnlockTime(id, timestamp);
        }

        var display = GetString(value, "display_name") ??
                      GetString(value, "DisplayName") ??
                      GetString(value, "displayName") ??
                      GetLocalizedString(value, "UnlockedDisplayName") ??
                      GetLocalizedString(value, "DisplayName") ??
                      GetString(value, "name");

        if (!string.IsNullOrWhiteSpace(display))
        {
            result.DisplayNames.Add(display!);

            if (timestamp > 0)
            {
                result.AddUnlockTime(display, timestamp);
            }
        }

        result.Sources.Add("Epic (Nemirtingas)");
    }

    // ---------------------------------------------------------------------
    // Shared helpers
    // ---------------------------------------------------------------------

    private static IEnumerable<string> GetSearchDirectories(Game game)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var install = TrimPath(game.InstallPath ?? string.Empty);

        string? current = null;
        try
        {
            current = Path.GetDirectoryName(game.ExecutablePath);
        }
        catch
        {
            // Fall back to the install folder below.
        }

        for (var level = 0; level < MaxParentLevels && !string.IsNullOrWhiteSpace(current); level++)
        {
            var directory = TrimPath(current!);
            if (directory.Length > 0 && seen.Add(directory))
            {
                yield return directory;
            }

            if (string.Equals(directory, install, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            try
            {
                current = Path.GetDirectoryName(directory);
            }
            catch
            {
                current = null;
            }
        }

        if (install.Length > 0 && seen.Add(install))
        {
            yield return install;
        }

        if (install.Length == 0)
        {
            yield break;
        }

        foreach (var child in new[]
                 {
                     "bin", "bin_plus", "Bin", "Binaries\\Win64",
                     "Engine\\Binaries\\Win64",
                     "Engine\\Binaries\\ThirdParty\\EOS\\Win64",
                     "nepice_settings",
                 })
        {
            var path = Path.Combine(install, child);
            if (seen.Add(path))
            {
                yield return path;
            }
        }
    }

    private static string? ReadIniValue(string path, string key)
    {
        try
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                if (line.Substring(0, separator).Trim()
                    .Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring(separator + 1).Trim().Trim('"');
                }
            }
        }
        catch
        {
            // A missing or locked ini has no value.
        }

        return null;
    }

    private static string? ReadJsonString(string path, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(property, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadJsonPathString(string path, params string[] segments)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var element = document.RootElement;

            foreach (var segment in segments)
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    !element.TryGetProperty(segment, out element))
                {
                    return null;
                }
            }

            return element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetLocalizedString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in new[] { "default", "en", "ru" })
        {
            if (value.TryGetProperty(key, out var localized) &&
                localized.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(localized.GetString()))
            {
                return localized.GetString();
            }
        }

        return null;
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return false;
        }

        return value.ValueKind == JsonValueKind.True ||
               value.ValueKind == JsonValueKind.String &&
               (value.GetString() == "1" ||
                string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase));
    }

    private static long GetLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return long.MinValue;
        }

        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), out number))
        {
            return number;
        }

        return long.MinValue;
    }

    private static string TrimPath(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        return builder.ToString();
    }

    private static void CopyFileIfMissing(string source, string target)
    {
        try
        {
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(source) ||
                File.Exists(target))
            {
                return;
            }

            File.Copy(source, target);
        }
        catch
        {
            // The save-folder copy is enough; the game-folder copy is extra.
        }
    }

    private static void CopyIcons(string source, string target)
    {
        try
        {
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(source))
            {
                return;
            }

            Directory.CreateDirectory(target);

            foreach (var file in Directory.EnumerateFiles(source, "*.jpg"))
            {
                var destination = Path.Combine(target, Path.GetFileName(file));
                if (!File.Exists(destination))
                {
                    File.Copy(file, destination);
                }
            }
        }
        catch
        {
            // The save-folder copy is enough; the game-folder copy is extra.
        }
    }

    private static async Task DownloadIconAsync(
        string? url,
        string path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return;
            }

            using var response = await Icons
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var bytes = await response.Content
                .ReadAsByteArrayAsync()
                .ConfigureAwait(false);

            if (bytes.Length > 0)
            {
                File.WriteAllBytes(path, bytes);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A failed icon only leaves the placeholder empty.
        }
    }
}
