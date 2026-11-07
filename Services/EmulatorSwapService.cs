using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BagaGame.Models;

namespace BagaGame.Services;

/// <summary>Which platform loader the option replaces.</summary>
public enum EmulatorFamily
{
    Steam,
    Epic,
    Gog,
}

/// <summary>One loader the user can put into a game folder.</summary>
public sealed record EmulatorOption(
    EmulatorFamily Family,
    string Name,
    string Detail,
    string? Path,
    string Version,
    bool IsCurrent,
    bool IsBackup)
{
    /// <summary>True for a build that lives in the launcher cache and may be deleted.</summary>
    public bool IsDownloaded { get; init; }

    public string DisplayName
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Version) ? Name : Name + " " + Version;
            return FamilyName + " · " + name;
        }
    }

    public string FamilyName => Family switch
    {
        EmulatorFamily.Steam => "Steam",
        EmulatorFamily.Epic => "Epic (EOS)",
        _ => "GOG",
    };
}

public sealed record EmulatorTarget(EmulatorFamily Family, string Directory, string FileName)
{
    public string FilePath => Path.Combine(Directory, FileName);

    public string BackupPath => FilePath + EmulatorSwapService.BackupSuffix;
}

/// <summary>
/// Swaps the platform loader of a game while keeping the original file as
/// "&lt;name&gt;.bagagame-original.bak". Steam family replaces steam_api64.dll,
/// Epic replaces EOSSDK-Win64-Shipping.dll and GOG replaces Galaxy64.dll.
/// </summary>
public static class EmulatorSwapService
{
    public const string BackupSuffix = ".bagagame-original.bak";

    public static string CacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BagaGame",
        "emulators");

    /// <summary>Folder that holds one family of downloaded builds.</summary>
    public static string FamilyRoot(EmulatorFamily family)
        => Path.Combine(CacheRoot, family.ToString().ToLowerInvariant());

    // ------------------------------------------------------------------
    // Target discovery
    // ------------------------------------------------------------------

    public static EmulatorTarget? FindTarget(Game game, EmulatorFamily family)
    {
        if (game is null)
        {
            return null;
        }

        string? exeDirectory = null;
        try
        {
            exeDirectory = Path.GetDirectoryName(game.ExecutablePath);
        }
        catch
        {
            // A malformed path keeps the install candidates only.
        }

        var install = game.InstallPath;

        if (family == EmulatorFamily.Steam)
        {
            var directory = FindSteamDirectory(game);
            if (directory is null)
            {
                return null;
            }

            var name = File.Exists(Path.Combine(directory, "steam_api.dll")) &&
                       !File.Exists(Path.Combine(directory, "steam_api64.dll"))
                ? "steam_api.dll"
                : "steam_api64.dll";

            return new EmulatorTarget(family, directory, name);
        }

        var fileName = family == EmulatorFamily.Epic
            ? "EOSSDK-Win64-Shipping.dll"
            : "Galaxy64.dll";

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(install))
        {
            if (family == EmulatorFamily.Epic)
            {
                candidates.Add(Path.Combine(install!, "Engine", "Binaries", "ThirdParty", "EOS", "Win64"));
            }

            candidates.Add(install!);
        }

        if (!string.IsNullOrWhiteSpace(exeDirectory))
        {
            candidates.Add(exeDirectory!);
        }

        // The launcher may treat a subfolder as the install root; the platform
        // file can live one or two levels above it.
        var current = !string.IsNullOrWhiteSpace(exeDirectory) ? exeDirectory : install;
        for (var level = 0; level < 3 && !string.IsNullOrWhiteSpace(current); level++)
        {
            var parent = Path.GetDirectoryName(current!);
            if (string.IsNullOrWhiteSpace(parent))
            {
                break;
            }

            if (!candidates.Contains(parent!, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(parent!);
            }

            current = parent;
        }

        foreach (var directory in candidates)
        {
            if (File.Exists(Path.Combine(directory, fileName)))
            {
                return new EmulatorTarget(family, directory, fileName);
            }
        }

        if (family == EmulatorFamily.Gog)
        {
            foreach (var directory in candidates)
            {
                if (File.Exists(Path.Combine(directory, "Galaxy.dll")))
                {
                    return new EmulatorTarget(family, directory, "Galaxy.dll");
                }
            }

            // A GOG release without the Galaxy SDK can still receive the
            // wrapper, but the game may never load it. The tab is shown so the
            // choice exists; the UI warns about it.
            foreach (var directory in candidates)
            {
                if (HasGogMarker(directory))
                {
                    return new EmulatorTarget(family, directory, "Galaxy64.dll");
                }
            }
        }

        return null;
    }

    private static bool HasGogMarker(string directory)
    {
        try
        {
            return Directory.GetFiles(directory, "goggame-*.info").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindSteamDirectory(Game game)
    {
        var directories = new List<string>();
        string? exeDirectory = null;

        try
        {
            exeDirectory = Path.GetDirectoryName(game.ExecutablePath);
            var stem = Path.GetFileNameWithoutExtension(game.ExecutablePath);
            if (!string.IsNullOrWhiteSpace(exeDirectory))
            {
                directories.Add(exeDirectory!);

                if (!string.IsNullOrWhiteSpace(stem))
                {
                    directories.Add(Path.Combine(exeDirectory!, stem + "_Data", "Plugins", "x86_64"));
                    directories.Add(Path.Combine(exeDirectory!, stem + "_Data", "Plugins"));
                }
            }
        }
        catch
        {
            // A malformed path only loses the exe candidates.
        }

        var install = game.InstallPath;
        if (!string.IsNullOrWhiteSpace(install))
        {
            directories.Add(install!);

            var steamworks = Path.Combine(
                install!,
                "Engine",
                "Binaries",
                "ThirdParty",
                "Steamworks");
            foreach (var version in SafeDirectories(steamworks))
            {
                directories.Add(Path.Combine(version, "Win64"));
                directories.Add(version);
            }
        }

        foreach (var directory in directories)
        {
            if (FindSteamLoader(directory) is not null)
            {
                return directory;
            }
        }

        var current = !string.IsNullOrWhiteSpace(exeDirectory) ? exeDirectory : install;
        for (var level = 0; level < 3 && !string.IsNullOrWhiteSpace(current); level++)
        {
            var parent = Path.GetDirectoryName(current!);
            if (string.IsNullOrWhiteSpace(parent))
            {
                break;
            }

            if (FindSteamLoader(parent!) is not null)
            {
                return parent;
            }

            current = parent;
        }

        if (!string.IsNullOrWhiteSpace(install))
        {
            try
            {
                var loaders = Directory
                    .EnumerateFiles(install!, "steam_api64.dll", SearchOption.AllDirectories)
                    .Where(path => path.IndexOf("_CommonRedist", StringComparison.OrdinalIgnoreCase) < 0)
                    .OrderBy(LoaderScore)
                    .ToList();

                if (loaders.Count > 0)
                {
                    return Path.GetDirectoryName(loaders[0]);
                }
            }
            catch
            {
                // An unreadable tree means there is no Steam loader.
            }
        }

        return null;
    }

    /// <summary>
    /// Prefers the loader used by the game over editor and tool copies such as
    /// "DevTools\bin\steam_api64.dll" (Dying Light 2 ships both).
    /// </summary>
    private static int LoaderScore(string path)
    {
        if (path.IndexOf("\\DevTools\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
            path.IndexOf("\\Editor", StringComparison.OrdinalIgnoreCase) >= 0 ||
            path.IndexOf("\\Tools\\", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return 100;
        }

        if (path.IndexOf("\\engine\\source\\bin", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return 0;
        }

        if (path.IndexOf("\\bin\\x64", StringComparison.OrdinalIgnoreCase) >= 0 ||
            path.IndexOf("\\Binaries\\Win64", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return 1;
        }

        return 10;
    }

    private static string? FindSteamLoader(string directory)
    {
        foreach (var name in new[] { "steam_api64.dll", "steam_api.dll" })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Options
    // ------------------------------------------------------------------

    /// <summary>Current loaders, their backups and local cracks, per family.</summary>
    public static IReadOnlyList<EmulatorOption> GetOptions(Game game)
    {
        var options = new List<EmulatorOption>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(EmulatorFamily family, string name, string detail, string path, bool current, bool backup, bool downloaded = false)
        {
            if (!File.Exists(path))
            {
                return;
            }

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch
            {
                return;
            }

            if (!paths.Add(full))
            {
                return;
            }

            var key = ContentKey(path);
            if (key is not null && !contents.Add(key))
            {
                return;
            }

            options.Add(new EmulatorOption(
                family,
                name,
                detail,
                path,
                VersionOf(path),
                current,
                backup)
            {
                IsDownloaded = downloaded,
            });
        }

        foreach (var family in new[] { EmulatorFamily.Steam, EmulatorFamily.Epic, EmulatorFamily.Gog })
        {
            var target = FindTarget(game, family);
            if (target is null)
            {
                continue;
            }

            Add(family, Loc.T("Loc.Emulators.CurrentFile"), target.Directory, target.FilePath, true, false);
            Add(family, Loc.T("Loc.Emulators.OriginalValve"), Path.GetFileName(target.BackupPath), target.BackupPath, false, true);

            if (family == EmulatorFamily.Steam)
            {
                foreach (var (fileName, label) in new[]
                         {
                             ("steam_api64.rne", "RUNE"),
                             ("steam_api.rne", "RUNE"),
                             ("steam_api64.cdx", "CODEX"),
                             ("steam_api.cdx", "CODEX"),
                         })
                {
                    Add(family, label, fileName, Path.Combine(target.Directory, fileName), false, false);
                }
            }

            foreach (var source in SafeDirectories(FamilyRoot(family)))
            {
                foreach (var build in SafeDirectories(source))
                {
                    foreach (var file in SafeFiles(build, target.FileName))
                    {
                        Add(
                            family,
                            NameForCachedBuild(source, build),
                            "BagaGame cache",
                            file,
                            false,
                            false,
                            true);
                    }
                }
            }
        }

        return options;
    }

    /// <summary>
    /// Collects loaders from other installed games: RUNE/CODEX, Goldberg,
    /// Nemirtingas EOS and UniverseLAN. Only known layouts are probed so the
    /// scan stays fast.
    /// </summary>
    public static IReadOnlyList<EmulatorOption> ScanLibrary(IEnumerable<Game> games, Game current)
    {
        var options = new List<EmulatorOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in games)
        {
            if (game is null ||
                string.Equals(game.Id, current.Id, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(game.InstallPath))
            {
                continue;
            }

            try
            {
                foreach (var directory in ProbeDirectories(game))
                {
                    foreach (var file in SafeFiles(directory))
                    {
                        var kind = Classify(file);
                        if (kind is null)
                        {
                            continue;
                        }

                        var full = Path.GetFullPath(file);
                        if (!seen.Add(full))
                        {
                            continue;
                        }

                        options.Add(new EmulatorOption(
                            kind.Value.Family,
                            kind.Value.Name,
                            game.Title,
                            file,
                            VersionOf(file),
                            false,
                            false));
                    }
                }
            }
            catch
            {
                // One broken game folder must not stop the catalog.
            }
        }

        return options;
    }

    private static IEnumerable<string> ProbeDirectories(Game game)
    {
        var directories = new List<string>();

        try
        {
            var exeDirectory = Path.GetDirectoryName(game.ExecutablePath);
            var stem = Path.GetFileNameWithoutExtension(game.ExecutablePath);
            if (!string.IsNullOrWhiteSpace(exeDirectory))
            {
                directories.Add(exeDirectory!);
                if (!string.IsNullOrWhiteSpace(stem))
                {
                    directories.Add(Path.Combine(exeDirectory!, stem + "_Data", "Plugins", "x86_64"));
                }
            }
        }
        catch
        {
            // The install folder below is still usable.
        }

        var install = game.InstallPath;
        if (string.IsNullOrWhiteSpace(install))
        {
            return directories;
        }

        directories.Add(install!);

        foreach (var version in SafeDirectories(Path.Combine(
                     install!,
                     "Engine",
                     "Binaries",
                     "ThirdParty",
                     "Steamworks")))
        {
            directories.Add(Path.Combine(version, "Win64"));
        }

        directories.Add(Path.Combine(
            install!,
            "Engine",
            "Binaries",
            "ThirdParty",
            "EOS",
            "Win64"));

        return directories;
    }

    private static (EmulatorFamily Family, string Name)? Classify(string file)
    {
        var name = Path.GetFileName(file);

        if (name.Equals("steam_api64.rne", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("steam_api.rne", StringComparison.OrdinalIgnoreCase))
        {
            return (EmulatorFamily.Steam, "RUNE");
        }

        if (name.Equals("steam_api64.cdx", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("steam_api.cdx", StringComparison.OrdinalIgnoreCase))
        {
            return (EmulatorFamily.Steam, "CODEX");
        }

        if (name.Equals("EOSSDK-Win64-Shipping.dll", StringComparison.OrdinalIgnoreCase))
        {
            return IsNemirtingas(file)
                ? (EmulatorFamily.Epic, "Nemirtingas Epic")
                : null;
        }

        if (name.Equals("Galaxy64.dll", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Galaxy.dll", StringComparison.OrdinalIgnoreCase))
        {
            var directory = Path.GetDirectoryName(file);
            return directory is not null && File.Exists(Path.Combine(directory, "UniverseLAN.ini"))
                ? (EmulatorFamily.Gog, "UniverseLAN")
                : null;
        }

        if (name.Equals("steam_api64.dll", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("steam_api.dll", StringComparison.OrdinalIgnoreCase))
        {
            if (IsGoldberg(file))
            {
                return (EmulatorFamily.Steam, "Goldberg");
            }

            return HasRuneMarker(file)
                ? (EmulatorFamily.Steam, "RUNE")
                : null;
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Apply / restore
    // ------------------------------------------------------------------

    /// <summary>Copies the selected loader over the game one, backing up the old file once.</summary>
    public static void Apply(Game game, int appId, EmulatorOption option)
    {
        if (game is null)
        {
            throw new ArgumentNullException(nameof(game));
        }

        if (option?.Path is null || !File.Exists(option.Path) || option.IsCurrent)
        {
            throw new InvalidOperationException("Файл эмулятора недоступен.");
        }

        var target = FindTarget(game, option.Family) ??
                     throw new InvalidOperationException("Не найдена папка загрузчика " + option.FamilyName + ".");

        if (File.Exists(target.FilePath) && !File.Exists(target.BackupPath))
        {
            File.Copy(target.FilePath, target.BackupPath);
        }

        File.Copy(option.Path, target.FilePath, true);

        if (option.Family == EmulatorFamily.Steam && appId > 0)
        {
            WriteAppId(target.Directory, appId);
            WriteAppId(Path.GetDirectoryName(game.ExecutablePath), appId);
        }

        if (option.Family == EmulatorFamily.Gog)
        {
            CopyUniverseLanConfig(option.Path, target.Directory);
        }
    }

    public static bool HasBackup(Game game, EmulatorFamily family)
        => FindTarget(game, family) is { } target && File.Exists(target.BackupPath);

    public static bool IsOriginalActive(Game game, EmulatorFamily family)
        => FindTarget(game, family) is { } target &&
           File.Exists(target.BackupPath) &&
           SameContent(target.FilePath, target.BackupPath);

    public static bool Restore(Game game, EmulatorFamily family)
    {
        if (FindTarget(game, family) is not { } target || !File.Exists(target.BackupPath))
        {
            return false;
        }

        File.Copy(target.BackupPath, target.FilePath, true);
        return true;
    }

    /// <summary>Removes a downloaded build from the launcher cache.</summary>
    public static bool DeleteDownloaded(EmulatorOption option)
    {
        if (option is null || !option.IsDownloaded || string.IsNullOrWhiteSpace(option.Path))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(option.Path!);
            var folder = Path.GetDirectoryName(full);
            if (string.IsNullOrWhiteSpace(folder))
            {
                return false;
            }

            var root = Path.GetFullPath(CacheRoot) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Directory.Delete(folder!, true);

            var cache = Path.Combine(CacheRoot, "_cache");
            if (Directory.Exists(cache))
            {
                foreach (var archive in Directory.GetFiles(cache, "*" + Path.GetFileName(folder) + ".*"))
                {
                    File.Delete(archive);
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static string NameForCachedBuild(string source, string build)
    {
        var sourceName = Path.GetFileName(source) switch
        {
            "gbe" => "Goldberg",
            "gse" => "Goldberg GSE",
            "nemirtingas" => "Nemirtingas",
            "universelan" => "UniverseLAN",
            _ => Path.GetFileName(source),
        };

        return sourceName + " " + Path.GetFileName(build);
    }

    private static void CopyUniverseLanConfig(string emulatorPath, string targetDirectory)
    {
        try
        {
            var source = Path.Combine(Path.GetDirectoryName(emulatorPath)!, "UniverseLAN.ini");
            var destination = Path.Combine(targetDirectory, "UniverseLAN.ini");
            if (File.Exists(source) && !File.Exists(destination))
            {
                File.Copy(source, destination);
            }
        }
        catch
        {
            // The emulator creates its own config when the file is absent.
        }
    }

    private static bool IsGoldberg(string path)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            var company = info.CompanyName ?? string.Empty;
            return company.IndexOf("GSE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   company.IndexOf("Goldberg", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>RUNE loaders carry "VLV" in their DOS header instead of the usual stub text.</summary>
    private static bool HasRuneMarker(string path)    {
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

    private static bool IsNemirtingas(string path)    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            var text = (info.CompanyName ?? string.Empty) + " " + (info.FileDescription ?? string.Empty);
            if (text.IndexOf("Nemirtingas", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            var directory = Path.GetDirectoryName(path);
            return directory is not null &&
                   (File.Exists(Path.Combine(directory, "NemirtingasEpicEmu.json")) ||
                    Directory.Exists(Path.Combine(directory, "nepice_settings")));
        }
        catch
        {
            return false;
        }
    }

    private static string VersionOf(string path)
    {
        try
        {
            return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? ContentKey(string path)
    {
        try
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        }
        catch
        {
            return null;
        }
    }

    private static bool SameContent(string first, string second)
    {
        try
        {
            if (new FileInfo(first).Length != new FileInfo(second).Length)
            {
                return false;
            }

            var left = ContentKey(first);
            var right = ContentKey(second);
            return left is not null && string.Equals(left, right, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteAppId(string? directory, int appId)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            File.WriteAllText(Path.Combine(directory!, "steam_appid.txt"), appId.ToString());
        }
        catch
        {
            // A read-only folder keeps the existing appid file.
        }
    }

    private static IEnumerable<string> SafeDirectories(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            yield break;
        }

        string[] items;
        try
        {
            items = Directory.GetDirectories(path!);
        }
        catch
        {
            yield break;
        }

        foreach (var item in items)
        {
            yield return item;
        }
    }

    private static IEnumerable<string> SafeFiles(string directory, string? filter = null)
    {
        string[] files;
        try
        {
            files = filter is null
                ? Directory.GetFiles(directory)
                : Directory.GetFiles(directory, filter);
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            yield return file;
        }
    }
}
