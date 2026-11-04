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

    public bool HasData => ApiNames.Count > 0 || DisplayNames.Count > 0 || Sources.Count > 0;

    public bool IsUnlocked(string apiName, string displayName)
        => (!string.IsNullOrWhiteSpace(apiName) && ApiNames.Contains(apiName)) ||
           (!string.IsNullOrWhiteSpace(displayName) && DisplayNames.Contains(displayName)) ||
           (!string.IsNullOrWhiteSpace(apiName) &&
            ApiNames.Any(id => IsNumeric(id) && HasNumericSuffix(apiName, id)));

    public static bool IsNumeric(string value)
        => value.Length > 0 && value.All(char.IsDigit);

    private static bool HasNumericSuffix(string apiName, string number)
        => apiName.Length > number.Length &&
           apiName.EndsWith("_" + number, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads achievements written by the Steam emulators used by non-Steam copies:
/// CODEX, RUNE, Goldberg, FreeTP and OnlineFix. Cuphead keeps its progress in
/// the game save, so it gets a small save adapter as well.
/// </summary>
public sealed class LocalAchievementsService
{
    private const int CupheadAppId = 268910;
    private const int HellraiserAppId = 1551980;

    private static readonly int[] CupheadIsleOneBosses =
    {
        6, 7, 1428495827, 1429976377, 1430652919,
    };

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

    public LocalAchievements Get(Game game, int appId)
    {
        var result = new LocalAchievements();
        var effectiveAppId = appId > 0 ? appId : game.AppId;

        try
        {
            var common = GetCommonDocuments();
            if (common is not null && effectiveAppId > 0)
            {
                ReadSteamEmulatorIni(
                    Path.Combine(common, "Steam", "CODEX", effectiveAppId.ToString(), "achievements.ini"),
                    "CODEX",
                    result,
                    requireAchieved: true);

                ReadSteamEmulatorIni(
                    Path.Combine(common, "Steam", "RUNE", effectiveAppId.ToString(), "achievements.ini"),
                    "RUNE",
                    result,
                    requireAchieved: true);

                ReadOnlineFix(
                    Path.Combine(common, "OnlineFix", effectiveAppId.ToString(), "Stats", "Achievements.ini"),
                    result);
            }

            // FreeTP and the game-save adapters do not need an AppID: they
            // know the game from the install path or the title.
            ReadFreeTp(game, result);

            if (effectiveAppId > 0)
            {
                ReadGoldberg(game, effectiveAppId, result);
            }

            if (effectiveAppId == CupheadAppId || IsGameNamed(game, "Cuphead"))
            {
                ReadCupheadSaves(result);
            }

            if (effectiveAppId == HellraiserAppId || IsGameNamed(game, "Hellraiser"))
            {
                ReadHellraiserSave(result);
            }

            // Ubisoft and Nemirtingas Epic emulators keep their own files,
            // outside the Steam emulator tree.
            PlatformAchievementService.Read(game, result);
        }
        catch
        {
            // Local progress is optional: a missing or locked file is skipped.
        }

        return result;
    }

    /// <summary>
    /// A folder scan normally gives the game its folder title, but a repaired
    /// manual entry can carry the executable's product name instead. The
    /// install path still identifies the save adapter.
    /// </summary>
    private static bool IsGameNamed(Game game, string value)
        => Contains(game.Title, value) ||
           Contains(game.InstallPath, value) ||
           Contains(game.ExecutablePath, value);

    private static bool Contains(string? text, string value)
        => !string.IsNullOrWhiteSpace(text) &&
           text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

    private static void ReadSteamEmulatorIni(
        string path,
        string source,
        LocalAchievements result,
        bool requireAchieved)
    {
        if (!File.Exists(path))
        {
            return;
        }

        string? section = null;
        var achieved = false;

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();

            if (line.StartsWith("[", StringComparison.Ordinal) &&
                line.EndsWith("]", StringComparison.Ordinal))
            {
                Flush();
                section = line.Substring(1, line.Length - 2).Trim();
                achieved = false;
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line.Substring(0, separator).Trim();
            var value = line.Substring(separator + 1).Trim();

            if (key.Equals("Achieved", StringComparison.OrdinalIgnoreCase) &&
                (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)))
            {
                achieved = true;
            }
            else if (key.Equals("time", StringComparison.OrdinalIgnoreCase) &&
                     long.TryParse(value, out var seconds) && seconds > 0)
            {
                achieved = true;
            }
        }

        Flush();

        void Flush()
        {
            if (section is null || section.Length == 0 || !achieved)
            {
                return;
            }

            if (requireAchieved || IsAchievementSection(section))
            {
                result.ApiNames.Add(section);
                result.Sources.Add(source);
            }
        }
    }

    private static void ReadFreeTp(Game game, LocalAchievements result)
    {
        if (string.IsNullOrWhiteSpace(game.InstallPath))
        {
            return;
        }

        var path = FindFreeTpAchievements(game.InstallPath);
        if (path is null)
        {
            return;
        }

        foreach (var section in ReadSections(path))
        {
            if (!IsAchievementSection(section))
            {
                continue;
            }

            // FreeTP stores the visible title in most builds and an API id in
            // a few others; keep both so the Steam list can still be merged.
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

    private static void ReadOnlineFix(string path, LocalAchievements result)
    {
        if (!File.Exists(path))
        {
            return;
        }

        string? section = null;
        var achieved = false;

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();

            if (line.StartsWith("[", StringComparison.Ordinal) &&
                line.EndsWith("]", StringComparison.Ordinal))
            {
                Flush();
                section = line.Substring(1, line.Length - 2).Trim();
                achieved = false;
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line.Substring(0, separator).Trim();
            var value = line.Substring(separator + 1).Trim();

            if (key.Equals("Achieved", StringComparison.OrdinalIgnoreCase) &&
                (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)))
            {
                achieved = true;
            }
            else if (key.Equals("time", StringComparison.OrdinalIgnoreCase) &&
                     long.TryParse(value, out var seconds) && seconds > 0)
            {
                achieved = true;
            }
        }

        Flush();

        void Flush()
        {
            if (section is null || section.Length == 0 || !achieved)
            {
                return;
            }

            if (IsAchievementSection(section))
            {
                result.ApiNames.Add(section);
                result.Sources.Add("OnlineFix");
            }
        }
    }

    private static void ReadGoldberg(Game game, int appId, LocalAchievements result)
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new List<string>
        {
            Path.Combine(roaming, "Goldberg SteamEmu Saves", appId.ToString(), "achievements.json"),
            Path.Combine(roaming, "Goldberg SteamEmu Saves", appId.ToString(), "stats", "achievements.json"),
            Path.Combine(roaming, "GSE Saves", appId.ToString(), "achievements.json"),
            Path.Combine(roaming, "GSE Saves", appId.ToString(), "stats", "achievements.json"),
        };

        if (!string.IsNullOrWhiteSpace(game.InstallPath))
        {
            // Portable Goldberg setups put the save tree inside the game
            // folder. The plain "achievements.json" in the install root is a
            // schema (or another platform's state) and must not be read here.
            candidates.Add(Path.Combine(
                game.InstallPath,
                "Goldberg SteamEmu Saves",
                appId.ToString(),
                "achievements.json"));
            candidates.Add(Path.Combine(
                game.InstallPath,
                "GSE Saves",
                appId.ToString(),
                "achievements.json"));
        }

        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;

                var container = root;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("achievements", out var nested) &&
                    nested.ValueKind == JsonValueKind.Object)
                {
                    container = nested;
                }

                if (container.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var property in container.EnumerateObject())
                {
                    if (string.IsNullOrWhiteSpace(property.Name) ||
                        property.Name.Equals("steamid", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var unlocked = false;

                    if (property.Value.ValueKind == JsonValueKind.True)
                    {
                        unlocked = true;
                    }
                    else if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        if (property.Value.TryGetProperty("earned", out var earned) &&
                            earned.ValueKind == JsonValueKind.True)
                        {
                            unlocked = true;
                        }
                        else if (property.Value.TryGetProperty("earned_time", out var time) &&
                                 time.ValueKind == JsonValueKind.Number &&
                                 time.GetInt64() > 0)
                        {
                            unlocked = true;
                        }
                    }

                    if (unlocked)
                    {
                        result.ApiNames.Add(property.Name);
                        result.Sources.Add("Goldberg");
                    }
                }
            }
            catch
            {
                // A malformed local file must not hide the Steam progress.
            }
        }
    }

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

    private static void ReadCupheadSaves(LocalAchievements result)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Cuphead");

            if (!Directory.Exists(directory))
            {
                return;
            }

            var files = Directory
                .GetFiles(directory, "cuphead_player_data_v1_slot_*.sav")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();

            foreach (var file in files)
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var root = document.RootElement;

                if (root.TryGetProperty("hasBeatenAnyBossOnEasy", out var easy) &&
                    easy.ValueKind == JsonValueKind.True)
                {
                    result.ApiNames.Add("defeatboss");
                    result.Sources.Add("Cuphead save");
                }

                if (!root.TryGetProperty("levelDataManager", out var manager) ||
                    !manager.TryGetProperty("levelObjects", out var levels) ||
                    levels.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var completed = new HashSet<int>();
                foreach (var level in levels.EnumerateArray())
                {
                    if (!level.TryGetProperty("levelID", out var id) ||
                        id.ValueKind != JsonValueKind.Number ||
                        !level.TryGetProperty("completed", out var done) ||
                        done.ValueKind != JsonValueKind.True)
                    {
                        continue;
                    }

                    completed.Add(id.GetInt32());
                }

                // Level 6 is Botanic Panic, the first boss in Cuphead.
                if (completed.Contains(6))
                {
                    result.ApiNames.Add("defeatboss");
                    result.Sources.Add("Cuphead save");
                }

                if (CupheadIsleOneBosses.All(completed.Contains))
                {
                    result.ApiNames.Add("completeworld1");
                    result.Sources.Add("Cuphead save");
                }
            }
        }
        catch
        {
            // The save can be open while the game is writing it.
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
