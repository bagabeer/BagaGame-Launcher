using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BagaGame.Models;

namespace BagaGame.Services;

public sealed class LocalAchievements
{
    public HashSet<string> ApiNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Names used by emulators that store the visible achievement title rather
    /// than the Steam API id (FreeTP does this in some builds).
    /// </summary>
    public HashSet<string> DisplayNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Unlock times written by an emulator, keyed by API name or display name.
    /// Not every format stores one; the launcher records a first-seen time for
    /// the rest.
    /// </summary>
    public Dictionary<string, DateTimeOffset> UnlockTimes { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public bool HasData => ApiNames.Count > 0 || DisplayNames.Count > 0 || Sources.Count > 0;

    public bool IsUnlocked(string apiName, string displayName)
        => (!string.IsNullOrWhiteSpace(apiName) && ApiNames.Contains(apiName)) ||
           (!string.IsNullOrWhiteSpace(displayName) && DisplayNames.Contains(displayName)) ||
           (!string.IsNullOrWhiteSpace(apiName) &&
            ApiNames.Any(id => IsNumeric(id) && HasNumericSuffix(apiName, id)));

    public DateTimeOffset? GetUnlockTime(string? apiName, string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(apiName) &&
            UnlockTimes.TryGetValue(apiName, out var apiTime))
        {
            return apiTime;
        }

        if (!string.IsNullOrWhiteSpace(displayName) &&
            UnlockTimes.TryGetValue(displayName, out var displayTime))
        {
            return displayTime;
        }

        return null;
    }

    public void AddUnlockTime(string? key, long timestamp)
    {
        if (string.IsNullOrWhiteSpace(key) ||
            FromTimestamp(timestamp) is not { } time)
        {
            return;
        }

        UnlockTimes[key] = time;
    }

    /// <summary>
    /// Emulators use Unix seconds, Unix milliseconds or Windows FILETIME.
    /// Returns <c>null</c> for an impossible value instead of throwing.
    /// </summary>
    public static DateTimeOffset? FromTimestamp(long value)
    {
        if (value <= 0)
        {
            return null;
        }

        try
        {
            if (value > 100_000_000_000_000L)
            {
                return new DateTimeOffset(DateTime.FromFileTimeUtc(value)).ToLocalTime();
            }

            if (value > 10_000_000_000L)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime();
            }

            return DateTimeOffset.FromUnixTimeSeconds(value).ToLocalTime();
        }
        catch
        {
            return null;
        }
    }

    public static bool IsNumeric(string value)
        => value.Length > 0 && value.All(char.IsDigit);

    private static bool HasNumericSuffix(string apiName, string number)
        => apiName.Length > number.Length &&
           apiName.EndsWith("_" + number, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads achievements written by the Steam emulators used by non-Steam copies.
/// Besides CODEX, RUNE, Goldberg, FreeTP and OnlineFix this also understands the
/// layouts used by SmartSteamEmu, ALI213, EMPRESS, CreamAPI, SKIDROW, RLD!,
/// 3DM, Hoodlum, DARKSiDERS, PLAZA and the portable Goldberg layout.
/// </summary>
public sealed class LocalAchievementsService
{
    private const int HellraiserAppId = 1551980;

    private static readonly string[] HellraiserStoryAchievements =
    {
        "ach_we_have_such_sights_to_show",
        "ach_whats_in_the_box",
        "ach_not_leaving_us_so_soon",
        "ach_the_box_you_opened_it",
        "ach_no_tears_please_its_a_waste",
        "ach_only_nightmares_last_forever",
        "ach_run_aidan_run",
        "ach_demons_to_some_angels_to",
        "ach_explorers_in_the_further",
        "ach_no_life_in_the_void_only",
        "ach_welcome_to_oblivion",
        "ach_puts_more_flesh_on_my_bones",
        "ach_come_to_daddy",
        "ach_red_dead_damnation",
        "ach_burn_oh_such_a_limited",
    };

    /// <summary>Steam emulator groups that keep "Steam\&lt;group&gt;\&lt;appid&gt;".</summary>
    private static readonly string[] SteamGroups =
    {
        "CODEX", "RUNE", "PLAZA", "EMPRESS", "FLT", "TiNYiSO", "SKIDROW",
        "Hoodlum", "DARKSiDERS", "3DM", "RLD!", "CPY", "PROPHET",
    };

    /// <summary>Groups that keep their own top-level folder with appid children.</summary>
    private static readonly string[] OtherGroups =
    {
        "SmartSteamEmu", "EMPRESS", "CreamAPI", "SKIDROW", "RLD!", "3DM",
        "Hoodlum", "DARKSiDERS", "ALI213", "PLAZA", "FLT", "TiNYiSO", "CPY", "PROPHET",
    };

    public LocalAchievements Get(Game game, int appId)
    {
        var result = new LocalAchievements();
        var effectiveAppId = appId > 0 ? appId : game.AppId;

        try
        {
            if (effectiveAppId > 0)
            {
                ReadEmulatorStates(game, effectiveAppId, result);
                ReadAnadiusEa(game, effectiveAppId, result);
            }

            ReadFreeTp(game, result);
            ReadRuneUbisoft(game, result);
            ReadGogUniverselan(game, result);

            if (effectiveAppId == HellraiserAppId || IsGameNamed(game, "Hellraiser"))
            {
                ReadHellraiserSave(result);
            }

            PlatformAchievementService.Read(game, result);
        }
        catch
        {
            // Local progress is optional: a missing or locked file is skipped.
        }

        return result;
    }

    // ------------------------------------------------------------------
    // Steam emulators
    // ------------------------------------------------------------------

    private static void ReadEmulatorStates(Game game, int appId, LocalAchievements result)
    {
        var roots = new List<string>();
        AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments));
        AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        AddRoot(roots, game.InstallPath);
        try
        {
            AddRoot(roots, Path.GetDirectoryName(game.ExecutablePath));
        }
        catch
        {
            // The executable path is optional.
        }

        var app = appId.ToString();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            foreach (var group in SteamGroups)
            {
                AddIni(Path.Combine(root, "Steam", group, app, "achievements.ini"), group);
                AddJson(Path.Combine(root, "Steam", group, app, "achievements.json"), group);
            }

            AddIni(Path.Combine(root, "OnlineFix", app, "Stats", "Achievements.ini"), "OnlineFix");
            AddJson(Path.Combine(root, "OnlineFix", app, "Stats", "Achievements.json"), "OnlineFix");

            AddJson(Path.Combine(root, "Goldberg SteamEmu Saves", app, "achievements.json"), "Goldberg");
            AddJson(Path.Combine(root, "Goldberg SteamEmu Saves", app, "stats", "achievements.json"), "Goldberg");
            AddJson(Path.Combine(root, "GSE Saves", app, "achievements.json"), "GSE");
            AddJson(Path.Combine(root, "GSE Saves", app, "stats", "achievements.json"), "GSE");

            // Portable layout: "<game>\<appid>\achievements.json" (Cuphead).
            AddJson(Path.Combine(root, app, "achievements.json"), "Goldberg");
            AddJson(Path.Combine(root, app, "stats", "achievements.json"), "Goldberg");
            AddIni(Path.Combine(root, app, "achievements.ini"), "Steam emulator");

            // Emulators whose cfg lives next to the executable (ALI213 and
            // similar) keep the state in the same folder.
            AddIni(Path.Combine(root, "achievements.ini"), "Steam emulator");
            AddIni(Path.Combine(root, "stats.ini"), "Steam emulator");

            foreach (var group in OtherGroups)
            {
                AddIni(Path.Combine(root, group, app, "achievements.ini"), group);
                AddJson(Path.Combine(root, group, app, "achievements.json"), group);
                AddIni(Path.Combine(root, group, app, "stats.ini"), group);
            }
        }

        // %ProgramData%\Steam\<any group>\<appid> (CODEX/RUNE variants).
        var programDataSteam = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Steam");
        foreach (var group in SafeDirectories(programDataSteam))
        {
            var name = Path.GetFileName(group);
            AddIni(Path.Combine(group, app, "achievements.ini"), name);
            AddJson(Path.Combine(group, app, "achievements.json"), name);
        }

        void AddIni(string path, string source)
        {
            if (seen.Add(path) && File.Exists(path))
            {
                ReadIniState(path, source, result);
            }
        }

        void AddJson(string path, string source)
        {
            if (seen.Add(path) && File.Exists(path))
            {
                ReadJsonState(path, source, result);
            }
        }
    }

    private static void ReadIniState(string path, string source, LocalAchievements result)
    {
        try
        {
            string? section = null;
            var achieved = false;
            long? unlockTimestamp = null;

            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();

                if (line.StartsWith("[", StringComparison.Ordinal) &&
                    line.EndsWith("]", StringComparison.Ordinal))
                {
                    Flush();
                    section = line.Substring(1, line.Length - 2).Trim();
                    achieved = false;
                    unlockTimestamp = null;
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, separator).Trim();
                var value = line.Substring(separator + 1).Trim();

                if ((key.Equals("Achieved", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("Earned", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("Unlocked", StringComparison.OrdinalIgnoreCase)) &&
                    (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)))
                {
                    achieved = true;
                }
                else if ((key.Equals("time", StringComparison.OrdinalIgnoreCase) ||
                          key.Equals("UnlockTime", StringComparison.OrdinalIgnoreCase) ||
                          key.Equals("earned_time", StringComparison.OrdinalIgnoreCase)) &&
                         long.TryParse(value, out var seconds) && seconds > 0)
                {
                    achieved = true;
                    unlockTimestamp = seconds;
                }
            }

            Flush();

            void Flush()
            {
                if (section is null || section.Length == 0 || !achieved ||
                    !IsAchievementSection(section))
                {
                    return;
                }

                result.ApiNames.Add(section);
                result.Sources.Add(source);

                if (unlockTimestamp is > 0)
                {
                    result.AddUnlockTime(section, unlockTimestamp.Value);
                }
            }
        }
        catch
        {
            // A malformed or locked state file is skipped.
        }
    }

    private static void ReadJsonState(string path, string source, LocalAchievements result)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("achievements", out var nested) &&
                nested.ValueKind == JsonValueKind.Object)
            {
                root = nested;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                {
                    ReadJsonEntry(property.Name, property.Value, source, result);
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

                    var id = GetString(item, "name") ??
                             GetString(item, "id") ??
                             GetString(item, "achievement_id") ??
                             GetString(item, "apiName");

                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        ReadJsonEntry(id!, item, source, result);
                    }
                }
            }
        }
        catch
        {
            // A malformed or locked state file is skipped.
        }
    }

    private static void ReadJsonEntry(
        string name,
        JsonElement value,
        string source,
        LocalAchievements result)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Equals("steamid", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var unlocked = false;
        string? display = null;
        var timestamp = 0L;

        if (value.ValueKind == JsonValueKind.True)
        {
            unlocked = true;
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            timestamp = Math.Max(timestamp, GetLong(value, "earned_time"));
            timestamp = Math.Max(timestamp, GetLong(value, "unlock_time"));
            timestamp = Math.Max(timestamp, GetLong(value, "time"));

            unlocked = GetBool(value, "earned") ||
                       GetBool(value, "achieved") ||
                       GetBool(value, "unlocked") ||
                       timestamp > 0;

            display = GetString(value, "displayName") ??
                      GetString(value, "display_name") ??
                      GetString(value, "name");
        }

        if (!unlocked)
        {
            return;
        }

        result.ApiNames.Add(name);

        if (timestamp > 0)
        {
            result.AddUnlockTime(name, timestamp);
        }

        if (!string.IsNullOrWhiteSpace(display))
        {
            result.DisplayNames.Add(display!);

            if (timestamp > 0)
            {
                result.AddUnlockTime(display, timestamp);
            }
        }

        result.Sources.Add(source);
    }

    // ------------------------------------------------------------------
    // Other platform emulators
    // ------------------------------------------------------------------

    /// <summary>
    /// Anadius EA/Origin emulator writes Achievement-Watcher-compatible files
    /// under "%LOCALAPPDATA%\anadius\LSX emu\achievement_watcher".
    /// </summary>
    private static void ReadAnadiusEa(Game game, int appId, LocalAchievements result)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "anadius",
            "LSX emu",
            "achievement_watcher");

        if (!Directory.Exists(root))
        {
            return;
        }

        var app = appId.ToString();
        var candidates = new[]
        {
            Path.Combine(root, app, "achievements.json"),
            Path.Combine(root, app, "stats", "achievements.json"),
            Path.Combine(root, "achievements.json"),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                ReadJsonState(path, "EA (Anadius)", result);
            }
        }

        // Some builds name the folder by the EA product id, not the Steam AppID.
        foreach (var directory in SafeDirectories(root))
        {
            var path = Path.Combine(directory, "achievements.json");
            if (File.Exists(path))
            {
                ReadJsonState(path, "EA (Anadius)", result);
            }
        }
    }

    /// <summary>
    /// RUNE's Ubisoft Connect emulator keeps
    /// "Documents\RUNE\Ubisoft Connect\achievements\&lt;user&gt;\&lt;uplayId&gt;\achievements.cfg".
    /// </summary>
    private static void ReadRuneUbisoft(Game game, LocalAchievements result)
    {
        var uplay = PlatformAchievementService.DetectUplay(game);
        if (uplay is null)
        {
            return;
        }

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RUNE",
            "Ubisoft Connect",
            "achievements");

        foreach (var user in SafeDirectories(root))
        {
            var path = Path.Combine(user, uplay.UplayId, "achievements.cfg");
            if (File.Exists(path))
            {
                ReadIniState(path, "Ubisoft (RUNE)", result);
            }
        }
    }

    /// <summary>
    /// GOG UniverseLAN keeps "%LOCALAPPDATA%\UniverseLAN\&lt;productId&gt;\UniverseLANData\Achievements.ini".
    /// The product id is taken from the game's "goggame-*.info" file.
    /// </summary>
    private static void ReadGogUniverselan(Game game, LocalAchievements result)
    {
        var info = FindFile(game, "goggame-*.info");
        if (info is null)
        {
            return;
        }

        var fileName = Path.GetFileNameWithoutExtension(info);
        if (string.IsNullOrWhiteSpace(fileName) ||
            !fileName.StartsWith("goggame-", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var productId = fileName.Substring("goggame-".Length);
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UniverseLAN",
            productId,
            "UniverseLANData",
            "Achievements.ini");

        if (File.Exists(path))
        {
            ReadIniState(path, "GOG (UniverseLAN)", result);
        }
    }

    private static string? FindFile(Game game, string pattern)
    {
        var directories = new List<string>();

        if (!string.IsNullOrWhiteSpace(game.InstallPath))
        {
            directories.Add(game.InstallPath!);
        }

        try
        {
            var directory = Path.GetDirectoryName(game.ExecutablePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                directories.Add(directory!);
            }
        }
        catch
        {
            // The executable path is optional.
        }

        foreach (var directory in directories)
        {
            try
            {
                var file = Directory.EnumerateFiles(directory, pattern).FirstOrDefault();
                if (file is not null)
                {
                    return file;
                }
            }
            catch
            {
                // A locked folder is skipped.
            }
        }

        return null;
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

    // ------------------------------------------------------------------
    // FreeTP
    // ------------------------------------------------------------------

    private static void ReadFreeTp(Game game, LocalAchievements result)
    {
        var paths = new List<string>();

        if (!string.IsNullOrWhiteSpace(game.InstallPath))
        {
            paths.Add(game.InstallPath!);
        }

        try
        {
            var directory = Path.GetDirectoryName(game.ExecutablePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                paths.Add(directory!);
            }
        }
        catch
        {
            // The executable path is optional.
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            var achievements = FindFreeTpAchievements(path);
            if (achievements is null || !seen.Add(achievements))
            {
                continue;
            }

            try
            {
                foreach (var section in ReadSections(achievements))
                {
                    if (!IsAchievementSection(section))
                    {
                        continue;
                    }

                    if (LooksLikeApiName(section))
                    {
                        result.ApiNames.Add(section);
                    }
                    else
                    {
                        result.DisplayNames.Add(section);
                    }

                    result.Sources.Add("FreeTP");
                }
            }
            catch
            {
                // A locked file is skipped.
            }
        }
    }

    /// <summary>
    /// FreeTP keeps "FreeTP\UserData\Achievements.ini" in the game root. The
    /// executable may live several levels down (ph_ft\work\bin\x64), so the
    /// ancestors are checked as well.
    /// </summary>
    private static string? FindFreeTpAchievements(string installPath)
    {
        try
        {
            var current = installPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);

            for (var level = 0; level < 8 && !string.IsNullOrWhiteSpace(current); level++)
            {
                var candidate = Path.Combine(
                    current,
                    "FreeTP",
                    "UserData",
                    "Achievements.ini");

                if (File.Exists(candidate))
                {
                    return candidate;
                }

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent) ||
                    string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = parent;
            }
        }
        catch
        {
            // A malformed path simply has no FreeTP data.
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Game save adapters
    // ------------------------------------------------------------------

    private static void ReadHellraiserSave(LocalAchievements result)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Hellraiser",
                "Saved",
                "SaveGames",
                "AchievementsSaveData.sav");

            if (!File.Exists(path))
            {
                return;
            }

            var bytes = File.ReadAllBytes(path);
            var text = System.Text.Encoding.ASCII.GetString(bytes);

            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(
                         text,
                         "ACH_[A-Z0-9_]+"))
            {
                result.ApiNames.Add(match.Value);
                result.Sources.Add("Hellraiser save");
            }

            var lastSave = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Hellraiser",
                "Saved",
                "SaveGames",
                "LastSave.sav");

            if (File.Exists(lastSave))
            {
                var lastText = System.Text.Encoding.ASCII.GetString(
                    File.ReadAllBytes(lastSave));

                if (lastText.IndexOf("MAP_Epilogue", StringComparison.Ordinal) >= 0 &&
                    lastText.IndexOf("Objective.Episode05", StringComparison.Ordinal) >= 0)
                {
                    foreach (var achievement in HellraiserStoryAchievements)
                    {
                        result.ApiNames.Add(achievement);
                    }

                    result.Sources.Add("Hellraiser story");
                }
            }
        }
        catch
        {
            // The game can be writing the save while it is running.
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static bool IsGameNamed(Game game, string value)
        => Contains(game.Title, value) ||
           Contains(game.InstallPath, value) ||
           Contains(game.ExecutablePath, value);

    private static bool Contains(string? text, string value)
        => !string.IsNullOrWhiteSpace(text) &&
           text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

    private static void AddRoot(List<string> roots, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var trimmed = path!.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);

            if (trimmed.Length > 0 &&
                !roots.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(trimmed);
            }
        }
        catch
        {
            // A malformed path is skipped.
        }
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> ReadSections(string path)
    {
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith("[", StringComparison.Ordinal) &&
                line.EndsWith("]", StringComparison.Ordinal))
            {
                var section = line.Substring(1, line.Length - 2).Trim();
                if (section.Length > 0)
                {
                    yield return section;
                }
            }
        }
    }

    private static bool IsAchievementSection(string section)
        => !section.Equals("SteamAchievements", StringComparison.OrdinalIgnoreCase)
           && !section.Equals("Settings", StringComparison.OrdinalIgnoreCase)
           && !section.Equals("Achievements", StringComparison.OrdinalIgnoreCase)
           && !section.Equals("Stats", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeApiName(string value)
        => value.All(character => char.IsLetterOrDigit(character) || character == '_')
           && value.IndexOf(' ') < 0
           && value.Any(char.IsLetter);

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

    private static string? GetCommonDocuments()
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            return path;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
        {
            return null;
        }

        var fallback = Path.GetFullPath(Path.Combine(profile, "..", "Public", "Documents"));
        return Directory.Exists(fallback) ? fallback : null;
    }
}
