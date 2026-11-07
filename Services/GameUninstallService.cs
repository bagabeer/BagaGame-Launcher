using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BagaGame.Models;
using Microsoft.Win32;

namespace BagaGame.Services;

public enum UninstallTargetKind
{
    Install,
    Emulator,
    Save,
    Steam,
}

public sealed record UninstallTarget(string Path, UninstallTargetKind Kind);

public sealed class GameUninstallPlan
{
    public List<UninstallTarget> Targets { get; } = new();

    public List<string> Notes { get; } = new();
}

/// <summary>
/// Builds and executes a full uninstall plan: game folder, known emulator data
/// for the AppID, and save/config folders matched by the game title. Every
/// target is validated and the warning window shows the exact list first.
/// </summary>
public static class GameUninstallService
{
    private static readonly string[] SteamGroups =
    {
        "CODEX", "RUNE", "PLAZA", "EMPRESS", "FLT", "TiNYiSO", "SKIDROW",
        "Hoodlum", "DARKSiDERS", "3DM", "RLD!", "CPY", "PROPHET",
    };

    private static readonly string[] OtherGroups =
    {
        "SmartSteamEmu", "EMPRESS", "CreamAPI", "SKIDROW", "RLD!", "3DM",
        "Hoodlum", "DARKSiDERS", "ALI213", "PLAZA", "FLT", "TiNYiSO", "CPY", "PROPHET",
    };

    private static readonly string[] ProtectedNames =
    {
        "microsoft", "windows", "steam", "steamapps", "common", "program files",
        "program files (x86)", "programdata", "packages", "temp", "tmp", "cache",
        "google", "mozilla", "nvidia", "amd", "intel", "discord", "obs-studio",
        "common files", "windowsapps", "system32", "syswow64",
    };

    public static GameUninstallPlan Build(Game game, int appId)
    {
        var plan = new GameUninstallPlan();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var install = NormalizeDirectory(game.InstallPath);

        if (!string.IsNullOrWhiteSpace(install))
        {
            if (Directory.Exists(install))
            {
                Add(install, UninstallTargetKind.Install, plan, seen);
            }
            else
            {
                plan.Notes.Add(install);
            }
        }

        if (appId > 0)
        {
            AddEmulatorData(appId, install, plan, seen);
            AddSteamUserData(appId, plan, seen);
        }

        AddUplayData(game, plan, seen);
        AddGogData(game, plan, seen);
        AddSaveFolders(game, install, plan, seen);

        return plan;
    }

    public static IReadOnlyList<string> Delete(GameUninstallPlan plan)
    {
        var errors = new List<string>();

        foreach (var target in plan.Targets
                     .OrderByDescending(item => item.Path.Length))
        {
            try
            {
                if (Directory.Exists(target.Path))
                {
                    Directory.Delete(target.Path, true);
                }
            }
            catch (Exception ex)
            {
                errors.Add(target.Path + ": " + ex.Message);
            }
        }

        return errors;
    }

    private static void AddEmulatorData(
        int appId,
        string? install,
        GameUninstallPlan plan,
        HashSet<string> seen)
    {
        var app = appId.ToString();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };

        foreach (var root in roots.Where(IsUsableRoot))
        {
            Add(Path.Combine(root, "OnlineFix", app), UninstallTargetKind.Emulator, plan, seen);

            foreach (var group in SteamGroups)
            {
                Add(Path.Combine(root, "Steam", group, app), UninstallTargetKind.Emulator, plan, seen);
                Add(Path.Combine(root, group, app), UninstallTargetKind.Emulator, plan, seen);
            }

            foreach (var group in OtherGroups)
            {
                Add(Path.Combine(root, group, app), UninstallTargetKind.Emulator, plan, seen);
            }

            Add(Path.Combine(root, "Goldberg SteamEmu Saves", app), UninstallTargetKind.Emulator, plan, seen);
            Add(Path.Combine(root, "GSE Saves", app), UninstallTargetKind.Emulator, plan, seen);
        }

        if (!string.IsNullOrWhiteSpace(install) && Directory.Exists(install))
        {
            // FreeTP and similar portable emulators keep their data inside the
            // game folder, so the install target already covers them.
            Add(Path.Combine(install, "FreeTP"), UninstallTargetKind.Emulator, plan, seen);
        }
    }

    private static void AddSteamUserData(
        int appId,
        GameUninstallPlan plan,
        HashSet<string> seen)
    {
        var steam = FindSteamPath();
        if (steam is null)
        {
            return;
        }

        var userData = Path.Combine(steam, "userdata");
        try
        {
            if (!Directory.Exists(userData))
            {
                return;
            }

            foreach (var user in Directory.GetDirectories(userData))
            {
                Add(Path.Combine(user, appId.ToString()), UninstallTargetKind.Steam, plan, seen);
            }
        }
        catch
        {
            // A locked Steam folder is skipped.
        }
    }

    private static void AddUplayData(
        Game game,
        GameUninstallPlan plan,
        HashSet<string> seen)
    {
        try
        {
            var uplay = PlatformAchievementService.DetectUplay(game);
            if (uplay is null)
            {
                return;
            }

            Add(uplay.SaveDirectory, UninstallTargetKind.Save, plan, seen);
        }
        catch
        {
            // Uplay detection is optional.
        }
    }

    private static void AddGogData(
        Game game,
        GameUninstallPlan plan,
        HashSet<string> seen)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(game.InstallPath))
            {
                return;
            }

            var info = Directory
                .EnumerateFiles(game.InstallPath, "goggame-*.info", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            if (info is null)
            {
                return;
            }

            var product = Path.GetFileNameWithoutExtension(info);
            if (!product.StartsWith("goggame-", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var id = product.Substring("goggame-".Length);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Add(Path.Combine(local, "UniverseLAN", id), UninstallTargetKind.Emulator, plan, seen);
        }
        catch
        {
            // GOG metadata is optional.
        }
    }

    private static void AddSaveFolders(
        Game game,
        string? install,
        GameUninstallPlan plan,
        HashSet<string> seen)
    {
        var keys = BuildTitleKeys(game);
        if (keys.Count == 0)
        {
            return;
        }

        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Saved Games"),
        };

        foreach (var root in roots.Where(IsUsableRoot).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AddMatches(root, depth: 1, keys, install, plan, seen);
        }
    }

    private static void AddMatches(
        string root,
        int depth,
        IReadOnlyList<string> keys,
        string? install,
        GameUninstallPlan plan,
        HashSet<string> seen)
    {
        try
        {
            foreach (var directory in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(directory);
                if (IsProtectedName(name))
                {
                    continue;
                }

                if (MatchesTitle(name, keys))
                {
                    Add(directory, UninstallTargetKind.Save, plan, seen);
                    continue;
                }

                if (depth > 1)
                {
                    continue;
                }

                AddMatches(directory, depth + 1, keys, install, plan, seen);
            }
        }
        catch
        {
            // A locked folder is skipped.
        }
    }

    private static List<string> BuildTitleKeys(Game game)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        AddKey(game.Title);
        AddKey(Path.GetFileName(game.InstallPath));
        AddKey(Path.GetFileNameWithoutExtension(game.ExecutablePath));

        return keys.Where(key => key.Length >= 4).ToList();

        void AddKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var cleaned = Regex.Replace(
                value.ToLowerInvariant(),
                @"\b(reloaded|definitive|complete|deluxe|ultimate|gold|enhanced|remastered|edition|repack)\b",
                " ");
            var normalized = Normalize(cleaned);
            if (normalized.Length < 4)
            {
                return;
            }

            keys.Add(normalized);

            var tokens = Regex
                .Split(cleaned, @"[^a-z0-9]+")
                .Where(token => token.Length > 0)
                .ToList();

            for (var count = 2; count <= Math.Min(4, tokens.Count); count++)
            {
                var prefix = string.Concat(tokens.Take(count));
                if (prefix.Length >= 6)
                {
                    keys.Add(prefix);
                }
            }
        }
    }

    private static bool MatchesTitle(string directoryName, IReadOnlyList<string> keys)
    {
        var candidate = Normalize(directoryName);
        if (candidate.Length < 4 || IsProtectedName(directoryName))
        {
            return false;
        }

        foreach (var key in keys)
        {
            if (candidate.Equals(key, StringComparison.Ordinal))
            {
                return true;
            }

            if (key.Length >= 7 &&
                (candidate.StartsWith(key, StringComparison.Ordinal) ||
                 key.StartsWith(candidate, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsProtectedName(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return ProtectedNames.Any(name =>
            normalized.Equals(name, StringComparison.Ordinal) ||
            normalized.StartsWith(name + " ", StringComparison.Ordinal));
    }

    private static void Add(
        string path,
        UninstallTargetKind kind,
        GameUninstallPlan plan,
        HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return;
        }

        if (!Directory.Exists(full) || !seen.Add(full))
        {
            return;
        }

        plan.Targets.Add(new UninstallTarget(full, kind));
    }

    private static string? NormalizeDirectory(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path)
                ? null
                : Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    private static string Normalize(string value)
        => new(value
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

    private static bool IsUsableRoot(string? path)
        => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);

    private static string? FindSteamPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = key?.GetValue("SteamPath") as string;
            return string.IsNullOrWhiteSpace(path) ? null : path!.Replace('/', '\\');
        }
        catch
        {
            return null;
        }
    }
}
