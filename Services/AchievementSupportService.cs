using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BagaGame.Models;

namespace BagaGame.Services;

/// <summary>What achievement support a game has on this PC.</summary>
public sealed class AchievementSupport
{
    public string Platform { get; init; } = string.Empty;

    public bool HasCatalog { get; init; }

    /// <summary>True only after the launcher actually showed an achievement toast.</summary>
    public bool Verified { get; init; }

    public int LocalCount { get; init; }

    public string StatusText { get; init; } = string.Empty;

    /// <summary>
    /// Text for the compact pre-launch toast. Empty when no Steam emulator
    /// was detected; otherwise it says whether that emulator is active.
    /// </summary>
    public string ScanText { get; init; } = string.Empty;
}

/// <summary>
/// Describes the achievement support of one game. A Steam catalogue, a detected
/// emulator or old local progress only means the launcher can read achievements.
/// Verified means the launcher really showed a toast for this game.
/// </summary>
public static class AchievementSupportService
{
    public static AchievementSupport? Describe(Game game, int appId, bool notificationSeen)
    {
        if (game is null)
        {
            return null;
        }

        var labels = new List<string>();
        RepackInfo? repack = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(game.InstallPath) &&
                !string.IsNullOrWhiteSpace(game.ExecutablePath))
            {
                repack = RepackSupportService.Detect(game.InstallPath!, game.ExecutablePath!);
                AddRepackLabels(labels, repack.Kind);
            }
        }
        catch
        {
            // A locked folder only hides the emulator name.
        }

        try
        {
            if (PlatformAchievementService.DetectUplay(game) is not null)
            {
                labels.Add("Ubisoft");
            }
        }
        catch
        {
            // No Uplay save tree.
        }

        AddPlatformMarkers(labels, game);
        AddActiveSteamLoader(labels, game);

        LocalAchievements local;
        try
        {
            local = new LocalAchievementsService().Get(game, appId);
        }
        catch
        {
            local = new LocalAchievements();
        }

        var localCount = local.ApiNames.Count > 0
            ? local.ApiNames.Count
            : local.DisplayNames.Count;

        // Only a real toast proves that notifications work. Achievements earned
        // before the launcher was installed must not flip this badge.
        var verified = notificationSeen;
        var hasCatalog = appId > 0;

        if (!hasCatalog && !verified && labels.Count == 0)
        {
            return null;
        }

        var platformParts = new List<string>(labels);
        foreach (var source in local.Sources)
        {
            var exists = platformParts.Any(part =>
                part.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith(source + " ", StringComparison.OrdinalIgnoreCase));

            if (!exists)
            {
                platformParts.Add(source);
            }
        }

        var platform = platformParts.Count > 0
            ? string.Join(" · ", platformParts)
            : HasSteamLoader(game)
                ? Loc.T("Loc.Achievements.Platform.Steam")
                : "-";

        var status = verified
            ? Loc.T("Loc.Achievements.Verified")
            : Loc.T("Loc.Achievements.NotVerified");

        return new AchievementSupport
        {
            Platform = platform,
            HasCatalog = hasCatalog,
            Verified = verified,
            LocalCount = localCount,
            StatusText = status,
            ScanText = BuildScanText(repack),
        };
    }

    /// <summary>
    /// "Emulator X found" only when X is the loader the game actually uses.
    /// A RUNE repack whose steam_api64.rne was never activated still talks to
    /// Valve, so claiming it was found would be misleading.
    /// </summary>
    private static string BuildScanText(RepackInfo? repack)
    {
        var name = repack is null ? null : EmulatorName(repack.Kind);
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        return RepackSupportService.IsEmulatorActive(repack!)
            ? Loc.Format("Loc.Achievements.ScanEmpty", name)
            : Loc.Format("Loc.Achievements.ScanInactive", name);
    }

    private static string? EmulatorName(RepackKind kind)
    {
        if ((kind & RepackKind.Rune) != 0)
        {
            return "RUNE";
        }

        if ((kind & RepackKind.Codex) != 0)
        {
            return "CODEX";
        }

        if ((kind & RepackKind.Goldberg) != 0)
        {
            return "Goldberg";
        }

        if ((kind & RepackKind.OnlineFix) != 0)
        {
            return "OnlineFix";
        }

        if ((kind & RepackKind.FreeTp) != 0)
        {
            return "FreeTP";
        }

        return null;
    }

    /// <summary>
    /// Detects the non-Steam platform emulators by their config/DLL markers in
    /// the game folder (Anadius EA, Nemirtingas Epic, UniverseLAN, Uplay).
    /// </summary>
    private static void AddPlatformMarkers(List<string> labels, Game game)
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
                if (File.Exists(Path.Combine(directory, "anadius.cfg")) ||
                    File.Exists(Path.Combine(directory, "anadius32.dll")) ||
                    File.Exists(Path.Combine(directory, "anadius64.dll")))
                {
                    Add("EA (Anadius)");
                }

                if (File.Exists(Path.Combine(directory, "NemirtingasEpicEmu.json")) ||
                    Directory.Exists(Path.Combine(directory, "nepice_settings")))
                {
                    Add("Epic (Nemirtingas)");
                }

                if (File.Exists(Path.Combine(directory, "UniverseLAN.ini")))
                {
                    Add("GOG (UniverseLAN)");
                }

                if (File.Exists(Path.Combine(directory, "uplay_r1_loader.dll")) ||
                    File.Exists(Path.Combine(directory, "uplay_r2_loader.dll")) ||
                    File.Exists(Path.Combine(directory, "upc_r1_loader.dll")) ||
                    File.Exists(Path.Combine(directory, "upc_r2_loader.dll")) ||
                    File.Exists(Path.Combine(directory, "uplay_r1_loader64.dll")) ||
                    File.Exists(Path.Combine(directory, "uplay_r2_loader64.dll")) ||
                    File.Exists(Path.Combine(directory, "upc_r1_loader64.dll")) ||
                    File.Exists(Path.Combine(directory, "upc_r2_loader64.dll")))
                {
                    var loader = new[]
                        {
                            "uplay_r1_loader.dll",
                            "uplay_r2_loader.dll",
                            "upc_r1_loader.dll",
                            "upc_r2_loader.dll",
                            "uplay_r1_loader64.dll",
                            "uplay_r2_loader64.dll",
                            "upc_r1_loader64.dll",
                            "upc_r2_loader64.dll",
                        }
                        .Select(name => Path.Combine(directory, name))
                        .FirstOrDefault(File.Exists);

                    labels.RemoveAll(label =>
                        label.StartsWith("Ubisoft", StringComparison.OrdinalIgnoreCase));

                    Add(loader is not null && IsGoldbergUplayLoader(loader)
                        ? "Ubisoft (Goldberg)"
                        : "Ubisoft");
                }

                if (IsGogCopy(directory))
                {
                    Add("GOG");
                }
            }
            catch
            {
                // A locked folder is skipped.
            }
        }

        void Add(string label)
        {
            if (!labels.Contains(label, StringComparer.OrdinalIgnoreCase))
            {
                labels.Add(label);
            }
        }
    }

    /// <summary>
    /// Names the Steam loader that is actually installed. Repacks often ship
    /// RUNE under the normal steam_api64.dll name without steam_emu.ini, so
    /// the version resource is the only marker left.
    /// </summary>
    private static void AddActiveSteamLoader(List<string> labels, Game game)
    {
        try
        {
            var target = EmulatorSwapService.FindTarget(game, EmulatorFamily.Steam);
            if (target is null || !File.Exists(target.FilePath))
            {
                return;
            }

            var info = FileVersionInfo.GetVersionInfo(target.FilePath);
            var company = info.CompanyName ?? string.Empty;
            var version = info.FileVersion ?? string.Empty;

            string? name = null;
            if (company.IndexOf("GSE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                company.IndexOf("Goldberg", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                name = "Goldberg";
            }
            else if (HasRuneMarker(target.FilePath) ||
                     version.StartsWith("06.", StringComparison.OrdinalIgnoreCase) ||
                     version.StartsWith("07.", StringComparison.OrdinalIgnoreCase) ||
                     version.StartsWith("08.", StringComparison.OrdinalIgnoreCase))
            {
                name = "RUNE";
            }

            if (name is not null &&
                !labels.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                labels.Add(name);
            }
        }
        catch
        {
            // A locked loader only loses its name.
        }
    }

    private static bool HasRuneMarker(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[256];
            var read = stream.Read(buffer, 0, buffer.Length);
            return Encoding.ASCII
                .GetString(buffer, 0, read)
                .IndexOf("VLV", StringComparison.Ordinal) >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsGoldbergUplayLoader(string path)
    {
        try
        {
            var text = Encoding.ASCII.GetString(File.ReadAllBytes(path));
            return text.IndexOf("Goldberg UplayEmu", StringComparison.Ordinal) >= 0 ||
                   text.IndexOf("Mr_Goldberg", StringComparison.Ordinal) >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static void AddRepackLabels(List<string> labels, RepackKind kind)    {
        if ((kind & RepackKind.OnlineFix) != 0)
        {
            labels.Add("OnlineFix");
        }

        if ((kind & RepackKind.Codex) != 0)
        {
            labels.Add("CODEX");
        }

        if ((kind & RepackKind.Rune) != 0)
        {
            labels.Add("RUNE");
        }

        if ((kind & RepackKind.Goldberg) != 0)
        {
            labels.Add("Goldberg");
        }

        if ((kind & RepackKind.FreeTp) != 0)
        {
            labels.Add("FreeTP");
        }
    }

    /// <summary>
    /// A GOG release ships "goggame-&lt;id&gt;.info" and the Galaxy client DLL.
    /// Without this marker a GOG repack was shown as "Steam" just because a
    /// Steam AppID catalogue existed for the title.
    /// </summary>
    private static bool IsGogCopy(string directory)
    {
        if (File.Exists(Path.Combine(directory, "Galaxy64.dll")) ||
            File.Exists(Path.Combine(directory, "Galaxy.dll")))
        {
            return true;
        }

        try
        {
            return Directory.GetFiles(directory, "goggame-*.info").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True for a folder that really contains a Steam client loader. Used only
    /// as the last resort for the platform label, so a GOG or other repack is
    /// not called "Steam" merely because the achievement catalogue exists.
    /// </summary>
    private static bool HasSteamLoader(Game game)
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
                if (File.Exists(Path.Combine(directory, "steam_api64.dll")) ||
                    File.Exists(Path.Combine(directory, "steam_api.dll")) ||
                    File.Exists(Path.Combine(directory, "steamclient64.dll")) ||
                    File.Exists(Path.Combine(directory, "steam_appid.txt")))
                {
                    return true;
                }
            }
            catch
            {
                // A locked folder is skipped.
            }
        }

        return false;
    }
}
