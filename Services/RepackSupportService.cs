using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BagaGame.Models;

namespace BagaGame.Services;

/// <summary>Steam emulator found in a non-Steam copy of a game.</summary>
[Flags]
public enum RepackKind
{
    None = 0,
    OnlineFix = 1,
    Codex = 2,
    Rune = 4,
    Goldberg = 8,
    FreeTp = 16,
}

/// <summary>What a folder scan learned about the repack and its Steam AppID.</summary>
public sealed record RepackInfo(
    RepackKind Kind,
    int AppId,
    string? FixDirectory,
    bool IsRune);

/// <summary>
/// Detects the Steam emulator used by a repack (OnlineFix, CODEX/RUNE,
/// Goldberg or FreeTP) and creates the folders and files it needs to store
/// achievements. Everything is idempotent and best effort: a read-only game
/// folder must never break the scan.
/// </summary>
public static class RepackSupportService
{
    private const int MaxParentLevels = 8;
    private const int MaxAppId = 9_999_999;

    private static readonly HttpClient Icons = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly object SchemaGate = new();
    private static readonly HashSet<int> SchemaInFlight = new();

    /// <summary>
    /// Detects every repack, fills in the Steam AppID the emulator is using
    /// and provisions the missing achievement storage. Called once per scan.
    /// </summary>
    public static void Prepare(IEnumerable<Game> games, CancellationToken cancellationToken)
    {
        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (game.Source != GameLibraryService.FolderSource && !game.IsManual)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(game.InstallPath) ||
                string.IsNullOrWhiteSpace(game.ExecutablePath))
            {
                continue;
            }

            try
            {
                var info = Detect(game.InstallPath, game.ExecutablePath);
                if (info.Kind == RepackKind.None)
                {
                    continue;
                }

                if (info.AppId > 0)
                {
                    game.AppId = info.AppId;
                }

                Provision(game, info);
            }
            catch
            {
                // A locked or read-only folder only skips provisioning.
            }
        }
    }

    /// <summary>
    /// Reads the emulator markers next to the executable and in the install
    /// root. Looking only at the executable folder is not enough: OnlineFix
    /// lives in "ph\work\bin\x64" while the game root is several levels up.
    /// </summary>
    public static RepackInfo Detect(string installPath, string executablePath)
    {
        var kinds = RepackKind.None;
        var isRune = false;
        string? fixDirectory = null;

        foreach (var directory in GetSearchDirectories(installPath, executablePath))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    var name = Path.GetFileName(file);

                    if (name.Equals("OnlineFix.ini", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("OnlineFix64.dll", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("OnlineFix.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        kinds |= RepackKind.OnlineFix;
                        fixDirectory ??= directory;
                        continue;
                    }

                    if (name.Equals("SteamFix.ini", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("SteamFix64.dll", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("SteamFix.dll", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("EpicFix64.dll", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("EpicFix.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        kinds |= RepackKind.FreeTp;
                        fixDirectory ??= directory;
                        continue;
                    }

                    if (name.Equals("steam_emu.ini", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("CODEX.ini", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("RUNE.ini", StringComparison.OrdinalIgnoreCase))
                    {
                        var content = ReadText(file);
                        var rune = name.StartsWith("RUNE", StringComparison.OrdinalIgnoreCase) ||
                                   content.IndexOf("RUNE", StringComparison.OrdinalIgnoreCase) >= 0;
                        isRune |= rune;
                        kinds |= rune ? RepackKind.Rune : RepackKind.Codex;
                        fixDirectory ??= directory;
                        continue;
                    }

                    // Goldberg forks rename their loader, but the version
                    // resource still names GSE/Goldberg, OnlineFix or FreeTP.
                    var library = DetectEmulatorLibrary(file, name);
                    if (library != RepackKind.None)
                    {
                        kinds |= library;
                        fixDirectory ??= directory;
                    }
                }

                if (Directory.Exists(Path.Combine(directory, "steam_settings")))
                {
                    kinds |= RepackKind.Goldberg;
                    fixDirectory ??= directory;
                }
            }
            catch
            {
                // One unreadable folder must not stop the other candidates.
            }
        }

        // FreeTP keeps its data in a "FreeTP" folder in the game root. A game
        // patched with a newer SteamFix may not leave the DLL name we know.
        if (kinds == RepackKind.None && HasFreeTpFolder(installPath))
        {
            kinds = RepackKind.FreeTp;
            fixDirectory ??= installPath;
        }

        if (kinds == RepackKind.None)
        {
            return new RepackInfo(RepackKind.None, 0, null, false);
        }

        var appId = ReadAppId(installPath, executablePath);
        return new RepackInfo(kinds, appId, fixDirectory, isRune);
    }

    /// <summary>
    /// Provisioning pass for a game whose AppID was resolved after the scan
    /// (the store lookup can find it later). No-op when nothing is detected.
    /// </summary>
    public static void EnsureProvisioned(Game game, int appId)
    {
        if (game is null || appId <= 0 ||
            string.IsNullOrWhiteSpace(game.InstallPath) ||
            string.IsNullOrWhiteSpace(game.ExecutablePath))
        {
            return;
        }

        try
        {
            var info = Detect(game.InstallPath, game.ExecutablePath);
            if (info.Kind == RepackKind.None)
            {
                return;
            }

            if (game.AppId <= 0)
            {
                game.AppId = appId;
            }

            Provision(game, info with { AppId = appId });
        }
        catch
        {
            // A locked folder only skips the late provisioning pass.
        }
    }

    /// <summary>
    /// Downloads the Steam achievement schema for a Goldberg/GBE game and
    /// writes "steam_settings\achievements.json" plus local colour and grey
    /// icons. Without this file the emulator cannot enumerate achievements,
    /// so games such as Cyberpunk 2077 report none even when the runtime save
    /// exists. Runs after the scan and is skipped once a schema is present.
    /// </summary>
    public static async Task ProvisionAchievementSchemaAsync(
        Game game,
        int appId,
        SteamAchievementsService steam,
        CancellationToken cancellationToken)
    {
        if (game is null || appId <= 0 ||
            string.IsNullOrWhiteSpace(game.InstallPath) ||
            string.IsNullOrWhiteSpace(game.ExecutablePath))
        {
            return;
        }

        RepackInfo info;
        try
        {
            info = Detect(game.InstallPath, game.ExecutablePath);
        }
        catch
        {
            return;
        }

        if ((info.Kind & RepackKind.Goldberg) == 0)
        {
            return;
        }

        var settings = ResolveSteamSettingsDirectory(info.FixDirectory, game.ExecutablePath);
        if (settings is null)
        {
            return;
        }

        var schemaPath = Path.Combine(settings, "achievements.json");
        try
        {
            if (File.Exists(schemaPath) && new FileInfo(schemaPath).Length > 2)
            {
                return;
            }
        }
        catch
        {
            return;
        }

        lock (SchemaGate)
        {
            if (!SchemaInFlight.Add(appId))
            {
                return;
            }
        }

        try
        {
            var result = await steam
                .GetAsync(appId, cancellationToken)
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

            var images = Path.Combine(settings, "images");
            Directory.CreateDirectory(images);

            var entries = achievements.Select(item =>
            {
                var file = SafeFileName(item.ApiName);
                return new
                {
                    name = item.ApiName,
                    displayName = string.IsNullOrWhiteSpace(item.Name) ? item.ApiName : item.Name,
                    description = item.Description ?? string.Empty,
                    hidden = "0",
                    icon = "images/" + file + ".jpg",
                    icongray = "images/" + file + "_gray.jpg",
                };
            }).ToList();

            var json = JsonSerializer.Serialize(
                entries,
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(schemaPath, json, new UTF8Encoding(false));
            EnsureFile(Path.Combine(settings, "steam_appid.txt"), appId + Environment.NewLine);

            using var gate = new SemaphoreSlim(6);
            await Task.WhenAll(achievements.Select(async item =>
            {
                var file = SafeFileName(item.ApiName);

                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await DownloadIconAsync(
                            item.IconOpen,
                            Path.Combine(images, file + ".jpg"),
                            cancellationToken)
                        .ConfigureAwait(false);

                    await DownloadIconAsync(
                            item.IconLocked,
                            Path.Combine(images, file + "_gray.jpg"),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Offline, a private profile or a missing Steam account: the
            // launcher keeps working and retries on the next scan.
        }
        finally
        {
            lock (SchemaGate)
            {
                SchemaInFlight.Remove(appId);
            }
        }
    }

    private static string? ResolveSteamSettingsDirectory(
        string? fixDirectory,
        string executablePath)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(fixDirectory))
        {
            candidates.Add(Path.Combine(fixDirectory, "steam_settings"));
        }

        try
        {
            var directory = Path.GetDirectoryName(executablePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                candidates.Add(Path.Combine(directory, "steam_settings"));
            }
        }
        catch
        {
            // The executable path is optional here.
        }

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        var target = candidates.FirstOrDefault();
        if (target is null)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(target);
            return target;
        }
        catch
        {
            return null;
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

            if (bytes.Length == 0)
            {
                return;
            }

            File.WriteAllBytes(path, bytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A failed icon only leaves the coloured square empty.
        }
    }

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

    /// <summary>
    /// Best-effort creation of the storage the emulators expect. Existing
    /// files are never overwritten, and a failing location is simply skipped.
    /// </summary>
    public static void Provision(Game game, RepackInfo info)
    {
        if (info.Kind == RepackKind.None)
        {
            return;
        }

        var appId = info.AppId > 0 ? info.AppId : game.AppId;
        if (appId <= 0)
        {
            return;
        }

        if ((info.Kind & RepackKind.OnlineFix) != 0)
        {
            ProvisionOnlineFix(appId, info.FixDirectory);
        }

        if ((info.Kind & RepackKind.Codex) != 0)
        {
            ProvisionSteamEmulator(appId, "CODEX");
        }

        if ((info.Kind & RepackKind.Rune) != 0)
        {
            ProvisionSteamEmulator(appId, "RUNE");
        }

        if ((info.Kind & RepackKind.Goldberg) != 0)
        {
            ProvisionGoldberg(appId, info.FixDirectory);
        }

        if ((info.Kind & RepackKind.FreeTp) != 0)
        {
            ProvisionFreeTp(game.InstallPath);
        }
    }

    private static void ProvisionOnlineFix(int appId, string? fixDirectory)
    {
        var common = GetCommonDocuments();
        if (common is null)
        {
            return;
        }

        var root = Path.Combine(common, "OnlineFix", appId.ToString());
        var stats = Path.Combine(root, "Stats");
        var saves = Path.Combine(root, "Saves");

        EnsureDirectory(stats);
        EnsureDirectory(saves);
        EnsureFile(Path.Combine(stats, "Achievements.ini"), string.Empty);
        EnsureFile(Path.Combine(stats, "Stats.ini"), "[Stats]" + Environment.NewLine);

        // The DLL is useless without its configuration. Only a missing file is
        // created; a shipped OnlineFix.ini is always left untouched.
        if (!string.IsNullOrWhiteSpace(fixDirectory) &&
            HasFile(fixDirectory, "OnlineFix64.dll") &&
            !File.Exists(Path.Combine(fixDirectory, "OnlineFix.ini")))
        {
            EnsureFile(
                Path.Combine(fixDirectory, "OnlineFix.ini"),
                "[Main]" + Environment.NewLine +
                "RealAppId=" + appId + Environment.NewLine +
                "FakeAppId=480" + Environment.NewLine +
                "BuildId=0" + Environment.NewLine +
                "InstallDir=" + Environment.NewLine +
                Environment.NewLine +
                "[Misc]" + Environment.NewLine +
                "ExtraProtection=false" + Environment.NewLine +
                "EmulateTicket=false" + Environment.NewLine +
                Environment.NewLine +
                "[Interfaces]" + Environment.NewLine +
                "Apps=true" + Environment.NewLine +
                "User=true" + Environment.NewLine +
                "Utils=true" + Environment.NewLine +
                "Storage=true" + Environment.NewLine +
                "UserStats=true" + Environment.NewLine +
                "Friends=true" + Environment.NewLine +
                "UGC=true" + Environment.NewLine +
                "Inventory=true" + Environment.NewLine +
                "AppTicket=true" + Environment.NewLine);
        }
    }

    private static void ProvisionSteamEmulator(int appId, string emulator)
    {
        var common = GetCommonDocuments();
        if (common is null)
        {
            return;
        }

        var root = Path.Combine(common, "Steam", emulator, appId.ToString());
        EnsureDirectory(root);
        EnsureFile(
            Path.Combine(root, "achievements.ini"),
            "[SteamAchievements]" + Environment.NewLine + "Count=0" + Environment.NewLine);
        EnsureFile(
            Path.Combine(root, "leaderboards.ini"),
            "[SteamLeaderboards]" + Environment.NewLine + "Count=0" + Environment.NewLine);
    }

    private static void ProvisionGoldberg(int appId, string? fixDirectory)
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var name in new[] { "Goldberg SteamEmu Saves", "GSE Saves" })
        {
            var root = Path.Combine(roaming, name, appId.ToString());
            EnsureDirectory(root);
            EnsureFile(
                Path.Combine(root, "achievements.json"),
                "{}" + Environment.NewLine);
        }

        if (!string.IsNullOrWhiteSpace(fixDirectory))
        {
            var settings = Path.Combine(fixDirectory, "steam_settings");
            if (Directory.Exists(settings))
            {
                EnsureFile(
                    Path.Combine(settings, "steam_appid.txt"),
                    appId.ToString() + Environment.NewLine);
            }
        }
    }

    private static void ProvisionFreeTp(string? installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return;
        }

        var root = FindFreeTpRoot(installPath) ?? Path.Combine(installPath, "FreeTP");
        var userData = Path.Combine(root, "UserData");
        EnsureDirectory(userData);
        EnsureDirectory(Path.Combine(root, "UserSaves"));
        EnsureDirectory(Path.Combine(root, "UserProfiles"));
        EnsureFile(Path.Combine(userData, "Achievements.ini"), string.Empty);
    }

    private static int ReadAppId(string installPath, string executablePath)
    {
        foreach (var directory in GetSearchDirectories(installPath, executablePath))
        {
            var appId = ReadAppIdFile(Path.Combine(directory, "steam_appid.txt")) ??
                        ReadAppIdFile(Path.Combine(directory, "steam_settings", "steam_appid.txt")) ??
                        ReadIniAppId(Path.Combine(directory, "OnlineFix.ini")) ??
                        ReadIniAppId(Path.Combine(directory, "SteamFix.ini")) ??
                        ReadIniAppId(Path.Combine(directory, "steam_emu.ini")) ??
                        ReadIniAppId(Path.Combine(directory, "CODEX.ini")) ??
                        ReadIniAppId(Path.Combine(directory, "RUNE.ini")) ??
                        0;

            if (appId > 0)
            {
                return appId;
            }
        }

        return 0;
    }

    private static int? ReadAppIdFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            foreach (var raw in File.ReadLines(path))
            {
                var value = raw.Trim();
                if (TryParseAppId(value, out var appId))
                {
                    return appId;
                }
            }
        }
        catch
        {
            // The file can be locked while the game is starting.
        }

        return null;
    }

    private static int? ReadIniAppId(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                var separator = line.IndexOf('=');
                if (separator <= 0 || line.StartsWith(";", StringComparison.Ordinal) ||
                    line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var key = line.Substring(0, separator).Trim();
                if (!key.Equals("RealAppId", StringComparison.OrdinalIgnoreCase) &&
                    !key.Equals("AppId", StringComparison.OrdinalIgnoreCase) &&
                    !key.Equals("SteamAppId", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = line.Substring(separator + 1).Trim().Trim('"');
                if (TryParseAppId(value, out var appId))
                {
                    return appId;
                }
            }
        }
        catch
        {
            // A malformed config is skipped; the title lookup still exists.
        }

        return null;
    }

    private static bool TryParseAppId(string value, out int appId)
    {
        appId = 0;
        return int.TryParse(value, out appId) && appId > 0 && appId <= MaxAppId;
    }

    private static IEnumerable<string> GetSearchDirectories(
        string installPath,
        string executablePath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;

        try
        {
            current = Path.GetDirectoryName(executablePath);
        }
        catch
        {
            // A malformed path falls back to the install folder below.
        }

        var install = TrimPath(installPath);

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

            var parent = Path.GetDirectoryName(directory);
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(TrimPath(parent!), directory, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        if (install.Length > 0 && seen.Add(install))
        {
            yield return install;
        }

        // Unity keeps the emulator next to the player, not next to the .exe:
        // "NoImNotAHuman_Data\Plugins\x86_64\steam_emu.ini". Unreal projects
        // may also sit one level below the resolved root.
        foreach (var layout in GetKnownLayouts(install, executablePath))
        {
            if (seen.Add(layout))
            {
                yield return layout;
            }
        }
    }

    private static IEnumerable<string> GetKnownLayouts(string install, string executablePath)
    {
        var layouts = new List<string>();
        var executableDirectory = string.Empty;
        var stem = string.Empty;

        try
        {
            executableDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty;
            stem = Path.GetFileNameWithoutExtension(executablePath);
        }
        catch
        {
            // A malformed executable path simply has no Unity layout.
        }

        if (executableDirectory.Length > 0 && stem.Length > 0)
        {
            layouts.Add(Path.Combine(executableDirectory, stem + "_Data", "Plugins", "x86_64"));
            layouts.Add(Path.Combine(executableDirectory, stem + "_Data", "Plugins", "x86"));
        }

        if (install.Length == 0)
        {
            return layouts;
        }

        layouts.Add(Path.Combine(install, "Plugins", "x86_64"));
        layouts.Add(Path.Combine(install, "Plugins", "x86"));
        layouts.Add(Path.Combine(install, "Binaries", "Win64"));

        foreach (var child in SafeEnumerateDirectories(install))
        {
            layouts.Add(Path.Combine(child, "Plugins", "x86_64"));
            layouts.Add(Path.Combine(child, "Plugins", "x86"));
            layouts.Add(Path.Combine(child, "Binaries", "Win64"));
        }

        AddSteamworksLayouts(layouts, install);

        return layouts;
    }

    /// <summary>
    /// Unreal keeps the Steam emulator in
    /// "Engine\Binaries\ThirdParty\Steamworks\Steamv157\Win64". Dead Island 2
    /// and many other UE titles put RUNE/CODEX there, so the version folder
    /// has to be expanded instead of guessing a single version name.
    /// </summary>
    private static void AddSteamworksLayouts(List<string> layouts, string install)
    {
        AddSteamworksVersionLayouts(layouts, Path.Combine(
            install,
            "Engine",
            "Binaries",
            "ThirdParty",
            "Steamworks"));

        foreach (var child in SafeEnumerateDirectories(install))
        {
            AddSteamworksVersionLayouts(layouts, Path.Combine(
                child,
                "Binaries",
                "ThirdParty",
                "Steamworks"));
        }
    }

    private static void AddSteamworksVersionLayouts(
        List<string> layouts,
        string steamworks)
    {
        if (!Directory.Exists(steamworks))
        {
            return;
        }

        foreach (var version in SafeEnumerateDirectories(steamworks))
        {
            layouts.Add(Path.Combine(version, "Win64"));
            layouts.Add(Path.Combine(version, "Win32"));
            layouts.Add(version);
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
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

    /// <summary>
    /// Reads the version resource of a Steam loader. Goldberg forks often ship
    /// a renamed DLL, so the company is more reliable than the file name.
    /// </summary>
    private static RepackKind DetectEmulatorLibrary(string path, string name)
    {
        if (!name.Equals("steam_api64.dll", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("steam_api.dll", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("steamclient64.dll", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("steamclient.dll", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("steam_api64.rne", StringComparison.OrdinalIgnoreCase))
        {
            return RepackKind.None;
        }

        try
        {
            var company = FileVersionInfo.GetVersionInfo(path).CompanyName ?? string.Empty;

            if (company.IndexOf("GSE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                company.IndexOf("Goldberg", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return RepackKind.Goldberg;
            }

            if (company.IndexOf("Online-Fix", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return RepackKind.OnlineFix;
            }

            if (company.IndexOf("FreeTP", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return RepackKind.FreeTp;
            }
        }
        catch
        {
            // The DLL can be locked while the game is updating.
        }

        return RepackKind.None;
    }

    private static string TrimPath(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool HasFreeTpFolder(string installPath)
    {
        try
        {
            return Directory.Exists(Path.Combine(installPath, "FreeTP"));
        }
        catch
        {
            return false;
        }
    }

    private static string? FindFreeTpRoot(string installPath)
    {
        var current = TrimPath(installPath);
        for (var level = 0; level < MaxParentLevels && current.Length > 0; level++)
        {
            if (Directory.Exists(Path.Combine(current, "FreeTP")))
            {
                return Path.Combine(current, "FreeTP");
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(TrimPath(parent!), current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = TrimPath(parent!);
        }

        return null;
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

        try
        {
            var fallback = Path.GetFullPath(Path.Combine(profile, "..", "Public", "Documents"));
            return Directory.Exists(fallback) ? fallback : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool HasFile(string directory, string name)
    {
        try
        {
            return File.Exists(Path.Combine(directory, name));
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
            // A protected or read-only game folder is skipped.
        }
    }

    private static void EnsureFile(string path, string content)
    {
        try
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path, content, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Same as EnsureDirectory: provisioning is optional.
        }
    }
}
