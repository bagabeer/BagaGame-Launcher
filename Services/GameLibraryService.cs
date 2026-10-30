using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using BagaGame.Compat;
using BagaGame.Models;
using Microsoft.Win32;

namespace BagaGame.Services;

public sealed record ScanOutcome(
    IReadOnlyList<Game> Games,
    IReadOnlyDictionary<string, int> SourceCounts);

/// <summary>
/// Finds locally installed games (Steam, Epic Games, GOG Galaxy and
/// user-selected folders) and downloads poster art from the Steam CDN.
/// </summary>
public sealed class GameLibraryService : IDisposable
{
    public const string SteamSource = "Steam";
    public const string EpicSource = "Epic Games";
    public const string GogSource = "GOG Galaxy";
    // Stable source ids: they are stored in ScanOutcome counts and must not
    // change with the interface language. Loc.SourceName() translates them.
    public const string FolderSource = "Folder";
    public const string ManualSource = "Manual";

    private static readonly string[] CoverUrls =
    {
        "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/library_600x900.jpg",
        "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/library_600x900_2x.jpg",
        "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/header.jpg",
    };

    // Posters are decorative: a broken or hostile response must not fill the
    // disk or exhaust memory. Steam's largest capsules stay well below these.
    private const long MaxCoverBytes = 12L * 1024 * 1024;
    private const int MaxCoverDimension = 8000;
    private const long MaxCoverPixels = 40_000_000L;

    // Image URLs come from a Steam API response, so only the Steam CDN may be
    // contacted even if the answer is tampered with.
    private static readonly HashSet<string> AllowedImageHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "shared.akamai.steamstatic.com",
        "cdn.akamai.steamstatic.com",
        "cdn.cloudflare.steamstatic.com",
        "shared.cloudflare.steamstatic.com",
        "store.steampowered.com",
        "steamcdn-a.akamaihd.net",
    };

    private static bool IsAllowedImageUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
           uri.Scheme == Uri.UriSchemeHttps &&
           AllowedImageHosts.Contains(uri.Host);

    // Steam tools/runtimes that are not games.
    private static readonly HashSet<int> NonGameSteamApps = new()
    {
        228980,  // Steamworks Common Redistributables
        1070560, // Steam Linux Runtime
        1391110, // Steam Linux Runtime - Soldier
        1493710, // Proton Experimental
        1628350, // Steam Linux Runtime - Sniper
        1887720, // Proton 7.0
        2348590, // Proton 8.0
        2805730, // Proton 9.0
    };

    private static readonly string[] NonGameSteamNames =
    {
        "Steamworks Common",
        "Steam Linux Runtime",
        "SteamVR",
        "Proton",
    };

    // Exact names only: a substring rule used to hide real games such as
    // CrashBandicoot.exe or UpdateMe.exe. Prefixes are reserved for files that
    // always carry a version suffix (unins000.exe, vcredist_x64.exe).
    private static readonly HashSet<string> IgnoredExecutableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "uninstall", "uninst", "setup", "install", "installer",
        "dxsetup", "dxwebsetup", "oalinst", "physx",
        "crashreportclient", "crashpad_handler",
        "unitycrashhandler32", "unitycrashhandler64",
        "update", "updater", "repair", "cleanup", "editor", "devtools",
        "activation", "activate", "register", "benchmark",
    };

    private static readonly string[] IgnoredExecutablePrefixes =
    {
        "unins", "uninstall", "vcredist", "dxsetup", "dxwebsetup",
        "ue4prereqsetup", "ue5prereqsetup", "dotnetfx", "ndp", "physx",
        "crashreport", "unitycrashhandler", "crashpad",
    };

    // Technical folders are skipped when looking for a human-readable game
    // name: "...\ph\work\bin\x64" -> "...\Dying Light 2 Stay Human".
    private static readonly HashSet<string> TechnicalFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "x64", "x86", "win64", "win32", "win", "bin", "binaries", "binary",
        "build", "builds", "release", "retail", "debug", "client", "app", "apps",
        "data", "engine", "ph", "work", "live", "shootergame", "content", "system",
        "support", "tools", "devtools", "redist", "_commonredist", "core", "modules",
        "platform", "launcher", "runtime", "packages", "shipping", "shippinggame",
        "ue4", "ue5", "gameplay", "source", "assets", "startup", "loader",
        "bootstrap", "amd64", "game",
    };

    // Climbing stops here: these folders contain many games, not one game.
    private static readonly HashSet<string> LibraryRootNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "games", "игры", "steam", "steamapps", "common", "epic games", "gog games",
        "gog galaxy", "riot games", "program files", "program files (x86)",
        "xboxgames", "ea games", "origin games", "ubisoft", "battle.net",
    };

    private static readonly HashSet<string> SkippedSearchFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "_commonredist", "redist", "redistributables", "directx", "vcredist",
        "support", "docs", "documentation", "devtools", "tools", "editor",
        "soundtrack", "mods", "sdk", "installers", "install",
    };

    private static readonly HashSet<string> GenericProductNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "game", "games", "client", "launcher", "application", "app", "unity",
        "unreal engine", "steam", "product name", "program",
    };

    private static readonly string[] UnusableProductFragments =
    {
        "redistributable", "directx", "visual c++", "dotnet", ".net runtime",
        "runtime", "framework", "installer", "setup", "uninstall", "updater",
        "crash", "report", "helper", "service", "driver", "editor", "engine",
        "benchmark", "sdk", "devtools",
    };

    // "Dying Light 2 Stay Human: Reloaded Edition" -> "Dying Light 2 Stay Human".
    private static readonly Regex EditionSuffixRegex = new(
        @"\s*[:\-–—]?\s*(?:(?:digital\s+deluxe|deluxe|ultimate|gold|definitive|complete|enhanced|standard|premium|special|collector'?s|anniversary|reloaded|limited|day\s+one)\s+edition|game\s+of\s+the\s+year(?:\s+edition)?|goty(?:\s+edition)?|director'?s\s+cut)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VersionSuffixRegex = new(
        @"\s+v\d+(?:\.\d+)+\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RepackTagRegex = new(
        @"[\[\(]\s*[^\[\]\(\)]*(?:repack|fitgirl|dodi|elamigos|xatab|rg\s+mechanics|portable|лицензия|торрент)[^\[\]\(\)]*[\]\)]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _storeSearchGate = new(3);
    private readonly SemaphoreSlim _genreGate = new(3);
    private readonly SemaphoreSlim _coverGate = new(6);
    private readonly Dictionary<string, int> _storeIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _storeSearchMisses = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _storeIdsLock = new();
    private string _coverCacheDirectory = DefaultCoverCacheDirectory;
    private bool _storeIdsLoaded;

    public GameLibraryService()
        : this(null)
    {
    }

    public GameLibraryService(string? cacheDirectory)
    {
        _coverCacheDirectory = NormalizeCacheDirectory(cacheDirectory);

        // A removed drive or a read-only folder must not stop the launcher:
        // fall back to the default cache location.
        if (!TryEnsureDirectory(_coverCacheDirectory))
        {
            _coverCacheDirectory = DefaultCoverCacheDirectory;
            TryEnsureDirectory(_coverCacheDirectory);
        }

        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BagaGame/0.6.0");
    }

    private static bool TryEnsureDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// <c>BagaGameData\covers</c> next to the executable (portable mode) or the
    /// per-user folder when the launcher cannot write there.
    /// </summary>
    public static string DefaultCoverCacheDirectory { get; } = AppPaths.DefaultCoverCacheDirectory;

    public string CoverCacheDirectory => _coverCacheDirectory;

    /// <summary>
    /// Switches the cover cache to another folder (for example a drive with more
    /// free space), copying the existing posters so nothing is re-downloaded.
    /// Returns <c>false</c> when the folder cannot be created.</summary>
    public bool TrySetCacheDirectory(string? directory)
    {
        var target = NormalizeCacheDirectory(directory);

        if (string.Equals(target, _coverCacheDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            Directory.CreateDirectory(target);
        }
        catch
        {
            return false;
        }

        MigrateCache(_coverCacheDirectory, target);
        _coverCacheDirectory = target;

        // The store lookup cache moved with the posters.
        lock (_storeIdsLock)
        {
            _storeIds.Clear();
            _storeSearchMisses.Clear();
            _storeIdsLoaded = false;
        }

        return true;
    }

    private static string NormalizeCacheDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return DefaultCoverCacheDirectory;
        }

        try
        {
            return Path.GetFullPath(directory.Trim());
        }
        catch
        {
            return DefaultCoverCacheDirectory;
        }
    }

    private static void MigrateCache(string source, string target)
    {
        try
        {
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(source))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(source))
            {
                var name = Path.GetFileName(file);
                if (!name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var destination = Path.Combine(target, name);
                if (File.Exists(destination))
                {
                    continue;
                }

                try
                {
                    File.Copy(file, destination);
                }
                catch
                {
                    // A locked file is re-downloaded on demand.
                }
            }
        }
        catch
        {
            // Migration is best effort; the cache simply starts empty.
        }
    }

    public void Dispose()
    {
        _storeSearchGate.Dispose();
        _genreGate.Dispose();
        _coverGate.Dispose();
        _http.Dispose();
    }

    /// <summary>
    /// GET with a couple of retries for flaky connections (timeouts, dropped
    /// requests, 429 and 5xx). Without this a single hiccup on someone else's
    /// machine would leave a card without its poster until the next scan.
    /// Returns <c>null</c> when every attempt failed; the caller treats that as
    /// "no data" and the library stays usable.
    /// </summary>
    private async Task<HttpResponseMessage?> GetWithRetryAsync(
        string url,
        CancellationToken cancellationToken,
        int attempts = 3)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);

                if (attempt < attempts && IsTransientStatus(response.StatusCode))
                {
                    response.Dispose();
                    await Task.Delay(
                            TimeSpan.FromMilliseconds(250 * attempt),
                            cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // HttpClient timeout rather than the user closing the window.
                if (attempt >= attempts)
                {
                    return null;
                }
            }
            catch (HttpRequestException)
            {
                if (attempt >= attempts)
                {
                    return null;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool IsTransientStatus(System.Net.HttpStatusCode status)
        => status == System.Net.HttpStatusCode.RequestTimeout ||
           status == (System.Net.HttpStatusCode)429 ||
           (int)status >= 500;

    /// <summary>
    /// Downloads a poster while limiting how many images are fetched at once,
    /// so a library with hundreds of games does not hammer the Steam CDN.
    /// </summary>
    private async Task<byte[]?> DownloadImageAsync(
        string url,
        CancellationToken cancellationToken)
    {
        await _coverGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var response = await GetWithRetryAsync(url, cancellationToken)
                .ConfigureAwait(false);

            if (response is null || !response.IsSuccessStatusCode)
            {
                return null;
            }

            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength.HasValue && declaredLength.Value > MaxCoverBytes)
            {
                return null;
            }

            var bytes = await response.Content
                .ReadAsByteArrayAsync()
                .ConfigureAwait(false);

            return bytes.Length >= 512 && bytes.Length <= MaxCoverBytes ? bytes : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            _coverGate.Release();
        }
    }

    /// <summary>Scans every known source. Never throws because of one broken store.</summary>
    public Task<ScanOutcome> ScanAsync(
        IReadOnlyCollection<string> extraFolders,
        CancellationToken cancellationToken)
        => Task.Run(() => ScanAll(extraFolders, cancellationToken), cancellationToken);

    private static ScanOutcome ScanAll(
        IReadOnlyCollection<string> extraFolders,
        CancellationToken cancellationToken)
    {
        var games = new List<Game>();

        Collect(games, "Steam", () => ScanSteam(cancellationToken), cancellationToken);
        Collect(games, "Epic Games", () => ScanEpic(cancellationToken), cancellationToken);
        Collect(games, "GOG Galaxy", () => ScanGog(cancellationToken), cancellationToken);
        Collect(games, FolderSource, () => ScanFolders(extraFolders, cancellationToken), cancellationToken);

        var unique = games
            .Where(game => !string.IsNullOrWhiteSpace(game.Id))
            .GroupBy(game => game.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var counts = unique
            .GroupBy(game => game.Source)
            .ToDictionary(group => group.Key, group => group.Count());

        return new ScanOutcome(unique, counts);
    }

    private static void Collect(
        List<Game> games,
        string source,
        Func<IEnumerable<Game>> scan,
        CancellationToken cancellationToken)
    {
        try
        {
            games.AddRange(scan());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A missing or locked store must not abort the rest of the scan.
            Debug.WriteLine($"{source} scan failed: {exception.Message}");
        }
    }

    // ---------------------------------------------------------------------
    // Steam
    // ---------------------------------------------------------------------

    private static IEnumerable<Game> ScanSteam(CancellationToken cancellationToken)
    {
        var games = new List<Game>();

        foreach (var steamRoot in GetSteamLibraryFolders())
        {
            var steamApps = Path.Combine(steamRoot, "steamapps");
            if (!Directory.Exists(steamApps))
            {
                continue;
            }

            IEnumerable<string> manifests;
            try
            {
                manifests = Directory.EnumerateFiles(steamApps, "appmanifest_*.acf");
            }
            catch
            {
                continue;
            }

            foreach (var manifest in manifests)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var content = File.ReadAllText(manifest);
                    var appIdText = ReadAcfValue(content, "appid");
                    var title = ReadAcfValue(content, "name");
                    var installDir = ReadAcfValue(content, "installdir");

                    if (!int.TryParse(appIdText, out var appId) ||
                        appId <= 0 ||
                        string.IsNullOrWhiteSpace(title) ||
                        string.IsNullOrWhiteSpace(installDir))
                    {
                        continue;
                    }

                    if (NonGameSteamApps.Contains(appId) ||
                        NonGameSteamNames.Any(name => title.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        continue;
                    }

                    var installPath = Path.Combine(steamApps, "common", installDir);
                    if (!Directory.Exists(installPath))
                    {
                        continue;
                    }

                    games.Add(new Game
                    {
                        Id = $"steam:{appId}",
                        AppId = appId,
                        Source = SteamSource,
                        Title = title,
                        InstallPath = installPath,
                    });
                }
                catch
                {
                    // A malformed manifest must not abort the whole scan.
                }
            }
        }

        return games;
    }

    private static IEnumerable<string> GetSteamLibraryFolders()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var steamPath = ReadRegistryValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath")
                        ?? ReadRegistryValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
                        ?? ReadRegistryValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath");

        if (!string.IsNullOrWhiteSpace(steamPath))
        {
            steamPath = steamPath.Replace('/', '\\');
            roots.Add(steamPath);
        }

        // Portable and re-installed Steam clients may be missing from the
        // registry on someone else's machine, so common locations are probed.
        foreach (var candidate in GetDefaultSteamPaths())
        {
            if (Directory.Exists(candidate))
            {
                roots.Add(candidate);
            }
        }

        foreach (var root in roots.ToList())
        {
            foreach (var libraryFile in new[]
                     {
                         Path.Combine(root, "steamapps", "libraryfolders.vdf"),
                         Path.Combine(root, "config", "libraryfolders.vdf"),
                     })
            {
                if (!File.Exists(libraryFile))
                {
                    continue;
                }

                try
                {
                    var content = File.ReadAllText(libraryFile);
                    foreach (Match match in Regex.Matches(content, "\"path\"\\s+\"([^\"]+)\""))
                    {
                        var path = match.Groups[1].Value.Replace("\\\\", "\\");
                        if (Directory.Exists(path))
                        {
                            roots.Add(path);
                        }
                    }
                }
                catch
                {
                    // Keep the main Steam folder even if the VDF is unreadable.
                }
            }
        }

        return roots;
    }

    private static IEnumerable<string> GetDefaultSteamPaths()
    {
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            candidates.Add(Path.Combine(programFilesX86, "Steam"));
        }

        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            candidates.Add(Path.Combine(programFiles, "Steam"));
        }

        foreach (var drive in new[] { "C:", "D:", "E:", "F:" })
        {
            candidates.Add($"{drive}\\Steam");
            candidates.Add($"{drive}\\SteamLibrary");
            candidates.Add($"{drive}\\Games\\Steam");
            candidates.Add($"{drive}\\SteamLibrary\\Steam");
        }

        return candidates;
    }

    // ---------------------------------------------------------------------
    // Epic Games
    // ---------------------------------------------------------------------

    private static IEnumerable<Game> ScanEpic(CancellationToken cancellationToken)
    {
        var games = new List<Game>();

        var manifestDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic",
            "EpicGamesLauncher",
            "Data",
            "Manifests");

        if (!Directory.Exists(manifestDirectory))
        {
            return games;
        }

        IEnumerable<string> manifests;
        try
        {
            manifests = Directory.EnumerateFiles(manifestDirectory, "*.item");
        }
        catch
        {
            return games;
        }

        foreach (var manifest in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                var root = document.RootElement;

                var title = GetJsonString(root, "DisplayName");
                var installPath = GetJsonString(root, "InstallLocation");
                var appName = GetJsonString(root, "AppName")
                              ?? GetJsonString(root, "CatalogItemId")
                              ?? Path.GetFileNameWithoutExtension(manifest);

                if (string.IsNullOrWhiteSpace(title) ||
                    string.IsNullOrWhiteSpace(installPath) ||
                    string.IsNullOrWhiteSpace(appName) ||
                    !Directory.Exists(installPath))
                {
                    continue;
                }

                games.Add(new Game
                {
                    Id = $"epic:{appName}",
                    Source = EpicSource,
                    Title = title,
                    InstallPath = installPath,
                    ExecutablePath = FindBestExecutable(installPath, cancellationToken: cancellationToken),
                });
            }
            catch
            {
                // Skip a single malformed Epic manifest.
            }
        }

        return games;
    }

    private static string? GetJsonString(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // GOG Galaxy
    // ---------------------------------------------------------------------

    private static IEnumerable<Game> ScanGog(CancellationToken cancellationToken)
    {
        var games = new List<Game>();

        foreach (var registryPath in new[]
                 {
                     @"SOFTWARE\WOW6432Node\GOG.com\Games",
                     @"SOFTWARE\GOG.com\Games",
                 })
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(registryPath);
                if (root is null)
                {
                    continue;
                }

                foreach (var subKeyName in root.GetSubKeyNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    using var gameKey = root.OpenSubKey(subKeyName);
                    if (gameKey is null)
                    {
                        continue;
                    }

                    var title = gameKey.GetValue("gameName") as string;
                    var installPath = gameKey.GetValue("path") as string;
                    var executable = gameKey.GetValue("exe") as string;
                    var gameId = gameKey.GetValue("gameID") as string ?? subKeyName;

                    if (string.IsNullOrWhiteSpace(title) ||
                        string.IsNullOrWhiteSpace(installPath) ||
                        !Directory.Exists(installPath))
                    {
                        continue;
                    }

                    games.Add(new Game
                    {
                        Id = $"gog:{gameId}",
                        Source = GogSource,
                        Title = title.Trim(),
                        InstallPath = installPath,
                        ExecutablePath = !string.IsNullOrWhiteSpace(executable) && File.Exists(executable)
                            ? executable
                            : FindBestExecutable(installPath, cancellationToken: cancellationToken),
                    });
                }
            }
            catch
            {
                // GOG is optional; keep scanning other stores.
            }
        }

        return games;
    }

    // ---------------------------------------------------------------------
    // User folders
    // ---------------------------------------------------------------------

    private enum FolderKind
    {
        /// <summary>The folder is one game (executable inside it).</summary>
        Game,

        /// <summary>The launcher lives in a subfolder: several games may hide here.</summary>
        Container,

        /// <summary>Nothing executable found.</summary>
        None,
    }

    private sealed record FolderProbe(
        FolderKind Kind,
        Game? Game,
        string? DeepExecutable,
        string? DeepTitle);

    // How many nested wrapper folders are unwrapped while looking for games
    // ("Games\Pack\Game1", "Games\Pack\Game2" ...).
    private const int MaxFolderDepth = 4;

    private static IEnumerable<Game> ScanFolders(
        IReadOnlyCollection<string> roots,
        CancellationToken cancellationToken)
    {
        var games = new List<Game>();

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(root))
            {
                continue;
            }

            var found = new List<Game>();
            var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            CollectFolderGames(root, 0, found, knownPaths, cancellationToken);

            // Nothing game-like was found below the root: the selected folder
            // itself may be one game (or hold exactly one game).
            if (found.Count == 0)
            {
                var probe = ProbeFolder(root, cancellationToken);
                if (probe.Kind == FolderKind.Game)
                {
                    AddFolderGame(found, knownPaths, probe.Game);
                }
                else if (probe.DeepExecutable is not null)
                {
                    AddFolderGame(
                        found,
                        knownPaths,
                        MakeFolderGame(
                            root,
                            probe.DeepTitle ?? CleanFolderTitle(Path.GetFileName(root)),
                            probe.DeepExecutable));
                }
            }

            games.AddRange(found);
        }

        return games;
    }

    /// <summary>
    /// Walks a library folder. A folder becomes a game when its launcher lives
    /// in it; when the launcher sits deeper ("...\DL2\Dying Light 2\ph\work\
    /// bin\x64\..."), the wrapper is treated as a container and its children
    /// are examined, so one wrapper can hide several games.
    /// </summary>
    private static void CollectFolderGames(
        string folder,
        int depth,
        List<Game> found,
        HashSet<string> knownPaths,
        CancellationToken cancellationToken)
    {
        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(folder);
        }
        catch
        {
            return;
        }

        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var probe = ProbeFolder(child, cancellationToken);

            if (probe.Kind == FolderKind.Game)
            {
                AddFolderGame(found, knownPaths, probe.Game);
                continue;
            }

            if (probe.Kind == FolderKind.Container && depth < MaxFolderDepth)
            {
                var before = found.Count;
                CollectFolderGames(child, depth + 1, found, knownPaths, cancellationToken);

                if (found.Count > before)
                {
                    continue;
                }
            }

            // Nothing game-like inside: keep the first game the deep probe
            // found, so a real game is not lost because its wrapper is odd.
            if (probe.DeepExecutable is not null)
            {
                AddFolderGame(
                    found,
                    knownPaths,
                    MakeFolderGame(
                        child,
                        probe.DeepTitle ?? CleanFolderTitle(Path.GetFileName(child)),
                        probe.DeepExecutable));
            }
        }
    }

    /// <summary>
    /// Decides how a folder should be treated: a single game, a container that
    /// needs another level of looking, or nothing at all. Only an executable
    /// directly in the folder makes it a game here; an executable one level
    /// down may belong to a child folder that is a game of its own.
    /// </summary>
    private static FolderProbe ProbeFolder(string folder, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? shallow;
        try
        {
            shallow = FindBestExecutable(folder, 0, cancellationToken);
        }
        catch
        {
            shallow = null;
        }

        if (shallow is not null)
        {
            return new FolderProbe(
                FolderKind.Game,
                MakeFolderGame(folder, CleanFolderTitle(Path.GetFileName(folder)), shallow),
                null,
                null);
        }

        if (IsSkippedScanChild(folder))
        {
            return new FolderProbe(FolderKind.None, null, null, null);
        }

        // Same lookup as "Добавить игру": it climbs out of bin\x64 and
        // Unreal "Binaries\Win64" layouts and reads the .exe metadata.
        Game? deep;
        try
        {
            deep = CreateManualGame(folder, cancellationToken);
        }
        catch
        {
            deep = null;
        }

        if (deep is null || string.IsNullOrWhiteSpace(deep.ExecutablePath))
        {
            return new FolderProbe(FolderKind.None, null, null, null);
        }

        if (!string.IsNullOrWhiteSpace(deep.InstallPath) && IsInside(folder, deep.InstallPath))
        {
            return new FolderProbe(FolderKind.Container, null, deep.ExecutablePath, deep.Title);
        }

        var installPath = ClampToScannedFolder(folder, deep.InstallPath);
        return new FolderProbe(
            FolderKind.Game,
            MakeFolderGame(installPath, deep.Title, deep.ExecutablePath),
            null,
            null);
    }

    private static Game MakeFolderGame(string installPath, string title, string executable)
        => new()
        {
            Id = $"folder:{installPath.ToLowerInvariant()}",
            Source = FolderSource,
            Title = string.IsNullOrWhiteSpace(title) ? CleanFolderTitle(Path.GetFileName(installPath)) : title,
            InstallPath = installPath,
            ExecutablePath = executable,
        };

    private static void AddFolderGame(List<Game> found, HashSet<string> knownPaths, Game? game)
    {
        if (game is null ||
            string.IsNullOrWhiteSpace(game.InstallPath) ||
            string.IsNullOrWhiteSpace(game.ExecutablePath))
        {
            return;
        }

        var key = game.InstallPath
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();

        if (knownPaths.Add(key))
        {
            found.Add(game);
        }
    }

    /// <summary>True when <paramref name="candidate"/> is a real subfolder of <paramref name="parent"/>.</summary>
    private static bool IsInside(string parent, string candidate)
    {
        var normalized = parent.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        if (candidate.Length <= normalized.Length ||
            !candidate.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var separator = candidate[normalized.Length];
        return separator == Path.DirectorySeparatorChar || separator == Path.AltDirectorySeparatorChar;
    }

    /// <summary>
    /// Guards the deep probe for a folder inside a user library: engine
    /// internals and nested stores are not games.
    /// </summary>
    private static bool IsSkippedScanChild(string folder)
    {
        var name = Path.GetFileName(folder);

        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        return IsTechnicalFolder(name) ||
               IsLibraryRootFolder(folder) ||
               SkippedSearchFolders.Contains(name) ||
               LibraryRootNames.Contains(name);
    }

    /// <summary>
    /// The resolved install root must stay inside the scanned folder; anything
    /// above it would merge neighbouring games into one card.
    /// </summary>
    private static string ClampToScannedFolder(string folder, string? resolved)
    {
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return folder;
        }

        try
        {
            var root = Path.GetFullPath(folder).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(resolved).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);

            // A plain prefix check would accept C:\Games\Foobar for C:\Games\Foo.
            if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase) ||
                IsInside(root, candidate))
            {
                return resolved;
            }
        }
        catch
        {
            // An invalid path is clamped to the scanned folder below.
        }

        return folder;
    }

    /// <summary>
    /// Finds a game executable. Folder scans look at the folder and one level
    /// of subfolders first, then fall back to a deeper probe (see
    /// <see cref="ScanFolders"/>), while adding a game by hand may look six
    /// levels down — many games keep the launcher in "bin\x64".
    /// </summary>
    private static string? FindBestExecutable(
        string folder,
        int maxDepth = 1,
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<string>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((folder, 0));
        var visited = 0;

        while (queue.Count > 0 && visited < 8000 && candidates.Count < 800)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (current, depth) = queue.Dequeue();
            visited++;

            try
            {
                candidates.AddRange(
                    Directory.EnumerateFiles(current, "*.exe", SearchOption.TopDirectoryOnly)
                        .Where(IsLikelyGameExecutable));
            }
            catch
            {
                // Unreadable folder: skip it.
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            try
            {
                foreach (var child in Directory.EnumerateDirectories(current))
                {
                    if (SkippedSearchFolders.Contains(Path.GetFileName(child)))
                    {
                        continue;
                    }

                    queue.Enqueue((child, depth + 1));
                }
            }
            catch
            {
                // Only the files found above will be considered.
            }
        }

        var folderName = NormalizeName(Path.GetFileName(folder) ?? string.Empty);

        return candidates
            .Where(IsLikelyGameExecutable)
            .OrderBy(path => ScoreExecutable(path, folderName))
            .ThenBy(path => HasProductTitle(path) ? 0 : 1)
            .ThenBy(path => path.Length)
            .FirstOrDefault();
    }

    private static bool IsLikelyGameExecutable(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (IgnoredExecutableNames.Contains(name))
        {
            return false;
        }

        return !IgnoredExecutablePrefixes.Any(
            prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static int ScoreExecutable(string path, string folderName)
    {
        var name = NormalizeName(Path.GetFileNameWithoutExtension(path));
        if (name.Length == 0)
        {
            return 10;
        }

        if (name == folderName)
        {
            return 0;
        }

        if (name.StartsWith(folderName, StringComparison.Ordinal) ||
            folderName.StartsWith(name, StringComparison.Ordinal))
        {
            return 1;
        }

        return 2;
    }

    private static string NormalizeName(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string CleanFolderTitle(string folderName)
    {
        var title = CleanGameTitle(folderName);
        return string.IsNullOrWhiteSpace(title) ? folderName.Trim() : title;
    }

    /// <summary>
    /// Removes edition/version suffixes and repack tags from a title, so
    /// "Dying Light 2 Stay Human Reloaded Edition" becomes a clean name that
    /// the Steam Store can find.
    /// </summary>
    private static string CleanGameTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var title = value.Replace('_', ' ');
        title = Regex.Replace(title, @"[™®©]", string.Empty);
        title = RepackTagRegex.Replace(title, " ");
        title = Regex.Replace(title, @"\s+", " ").Trim();
        title = EditionSuffixRegex.Replace(title, string.Empty).Trim();
        title = VersionSuffixRegex.Replace(title, string.Empty).Trim();
        title = Regex.Replace(title, @"[:\-–—]\s*$", string.Empty).Trim();

        return title.Length == 0 ? value.Replace('_', ' ').Trim() : title;
    }

    /// <summary>
    /// Turns the folder of an executable into the game root:
    /// "...\ph\work\bin\x64" -> "...\Dying Light 2 Stay Human Reloaded Edition".
    /// </summary>
    private static string ResolveInstallRoot(string executablePath)
    {
        var folder = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return string.Empty;
        }

        var current = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        for (var level = 0; level < 8; level++)
        {
            if (!IsTechnicalFolder(Path.GetFileName(current)) &&
                !IsEngineProjectFolder(current))
            {
                break;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent))
            {
                break;
            }

            parent = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var pathRoot = Path.GetPathRoot(parent);
            if (!string.IsNullOrEmpty(pathRoot) &&
                string.Equals(
                    parent,
                    pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (IsLibraryRootFolder(parent))
            {
                break;
            }

            current = parent;
        }

        return current;
    }

    private static bool IsTechnicalFolder(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        var trimmed = name.Trim();
        if (TechnicalFolderNames.Contains(trimmed))
        {
            return true;
        }

        // "Win64-Shipping", "Bin64", "Release_x64", "ph_ft", "v1.2.3".
        return Regex.IsMatch(
                   trimmed,
                   @"^(?:x|win|bin|binaries|build|release|retail|shipping)[\s_\-]*(?:32|64|x86|x64)?(?:[\s_\-]*(?:shipping|release|retail|final))?$",
                   RegexOptions.IgnoreCase) ||
               Regex.IsMatch(
                   trimmed,
                   @"^(?:ph|bin|binaries|win)[\s_\-]+[a-z0-9]{1,8}$",
                   RegexOptions.IgnoreCase) ||
               Regex.IsMatch(trimmed, @"^v?\d+(?:[._]\d+)*$", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Unreal Engine games keep their executables in
    /// "&lt;Project&gt;\Binaries\Win64", where "&lt;Project&gt;" is part of the
    /// engine layout rather than the install root. It is skipped, so Days Gone
    /// resolves to "...\Days Gone" and not to "...\Days Gone\BendGame".
    /// </summary>
    private static bool IsEngineProjectFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(Path.Combine(folder, "Binaries")))
            {
                return false;
            }

            if (Directory.Exists(Path.Combine(folder, "Content")))
            {
                return true;
            }

            // Unreal instalments also ship a shared "Engine" folder next to
            // the project folder.
            var parent = Path.GetDirectoryName(folder);
            return !string.IsNullOrWhiteSpace(parent) &&
                   Directory.Exists(Path.Combine(parent, "Engine"));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLibraryRootFolder(string folder)
    {
        if (LibraryRootNames.Contains(Path.GetFileName(folder)))
        {
            return true;
        }

        try
        {
            return Directory.Exists(Path.Combine(folder, "steamapps"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// A good title comes from the executable metadata (Dying Light 2's exe
    /// reports "Dying Light 2 Stay Human"); the folder name is the fallback.
    /// </summary>
    private static string ResolveManualTitle(string executablePath, string installRoot)
    {
        var productTitle = TryGetProductTitle(executablePath);
        if (!string.IsNullOrWhiteSpace(productTitle))
        {
            return productTitle;
        }

        var folderTitle = CleanFolderTitle(Path.GetFileName(installRoot));
        if (!string.IsNullOrWhiteSpace(folderTitle))
        {
            return folderTitle;
        }

        return CleanFolderTitle(Path.GetFileNameWithoutExtension(executablePath));
    }

    private static string? TryGetProductTitle(string executablePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);

            foreach (var candidate in new[] { info.ProductName, info.FileDescription })
            {
                if (!IsUsableProductTitle(candidate))
                {
                    continue;
                }

                var cleaned = CleanGameTitle(candidate!);
                if (cleaned.Length >= 2)
                {
                    return cleaned;
                }
            }
        }
        catch
        {
            // Missing or unreadable metadata: fall back to the folder name.
        }

        return null;
    }

    private static bool HasProductTitle(string executablePath)
        => !string.IsNullOrWhiteSpace(TryGetProductTitle(executablePath));

    private static bool IsUsableProductTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length < 2 || trimmed.Length > 100 || GenericProductNames.Contains(trimmed))
        {
            return false;
        }

        var lower = trimmed.ToLowerInvariant();
        return !UnusableProductFragments.Any(
            fragment => lower.IndexOf(fragment, StringComparison.Ordinal) >= 0);
    }

    // ---------------------------------------------------------------------
    // Games added by hand
    // ---------------------------------------------------------------------

    /// <summary>
    /// Builds a library entry from a folder or an .exe the user picked.
    /// Technical folders and executable metadata are used to show a readable
    /// title ("...\bin\x64\DyingLightGame.exe" -> "Dying Light 2 Stay Human").
    /// Returns <c>null</c> when the path is unusable or the folder has no
    /// executable that looks like a game.
    /// </summary>
    public static Game? CreateManualGame(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (File.Exists(path))
            {
                if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var executable = Path.GetFullPath(path);
                var installRoot = ResolveInstallRoot(executable);

                if (string.IsNullOrWhiteSpace(installRoot))
                {
                    return null;
                }

                return new Game
                {
                    Id = $"manual:{installRoot.ToLowerInvariant()}",
                    Source = ManualSource,
                    Title = ResolveManualTitle(executable, installRoot),
                    InstallPath = installRoot,
                    ExecutablePath = executable,
                };
            }

            if (!Directory.Exists(path))
            {
                return null;
            }

            var selectedPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var foundExecutable = FindBestExecutable(selectedPath, 8, cancellationToken);
            if (string.IsNullOrWhiteSpace(foundExecutable))
            {
                return null;
            }

            var installPath = ResolveInstallRoot(foundExecutable);

            return new Game
            {
                Id = $"manual:{installPath.ToLowerInvariant()}",
                Source = ManualSource,
                Title = ResolveManualTitle(foundExecutable, installPath),
                InstallPath = installPath,
                ExecutablePath = foundExecutable,
            };
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // Launch helper
    // ---------------------------------------------------------------------

    /// <summary>
    /// Returns the executable BagaGame expects to see running after starting
    /// the game. Folder and manual entries already carry it; Steam, Epic and
    /// GOG entries may not, so the install folder is probed (up to six levels,
    /// the same lookup "Добавить игру" uses). Used only to watch playtime.
    /// </summary>
    public string? ResolveLaunchExecutable(Game game)
    {
        if (!string.IsNullOrWhiteSpace(game.ExecutablePath) &&
            File.Exists(game.ExecutablePath))
        {
            return game.ExecutablePath;
        }

        if (!string.IsNullOrWhiteSpace(game.InstallPath) &&
            Directory.Exists(game.InstallPath))
        {
            try
            {
                return FindBestExecutable(game.InstallPath, 6);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // Covers
    // ---------------------------------------------------------------------

    /// <summary>
    /// Returns a portrait cover for any library entry. Steam games use their
    /// AppID, folder games first look for artwork next to the executable and
    /// then fall back to a Steam Store search by title. Epic and GOG titles use
    /// the same title lookup. Returns <c>null</c> when nothing matches.
    /// </summary>
    public async Task<System.Windows.Media.ImageSource?> GetCoverAsync(
        Game game,
        CancellationToken cancellationToken)
    {
        if (game.AppId > 0)
        {
            var steamCover = await GetSteamCoverAsync(game.AppId, cancellationToken);
            if (steamCover is not null)
            {
                return steamCover;
            }
        }

        if (!string.IsNullOrWhiteSpace(game.InstallPath))
        {
            var localPath = FindLocalCover(game.InstallPath);
            if (localPath is not null)
            {
                try
                {
                    return LoadFrozen(localPath);
                }
                catch
                {
                    // A broken image simply means: try the store lookup below.
                }
            }
        }

        var resolvedAppId = await ResolveSteamAppIdAsync(game, cancellationToken);
        return resolvedAppId > 0
            ? await GetSteamCoverAsync(resolvedAppId, cancellationToken)
            : null;
    }

    /// <summary>
    /// Returns the game's primary Steam genre as a canonical English name
    /// ("Action", "RPG", ...), or <c>null</c> when the game is not on Steam or
    /// the machine is offline. The interface translates it for display, so the
    /// stored value survives a language switch.
    /// </summary>
    public async Task<string?> GetGenreAsync(Game game, CancellationToken cancellationToken)
    {
        var appId = game.AppId;
        if (appId <= 0)
        {
            appId = await ResolveSteamAppIdAsync(game, cancellationToken).ConfigureAwait(false);
        }

        if (appId <= 0)
        {
            return null;
        }

        await _genreGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var detailsUrl =
                $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english&cc=US";

            using var response = await GetWithRetryAsync(detailsUrl, cancellationToken)
                .ConfigureAwait(false);

            if (response is null || !response.IsSuccessStatusCode)
            {
                return null;
            }

            using var stream = await response.Content
                .ReadAsStreamAsync()
                .ConfigureAwait(false);

            using var document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty(appId.ToString(), out var entry) ||
                !entry.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("genres", out var genres) ||
                genres.ValueKind != JsonValueKind.Array ||
                genres.GetArrayLength() == 0)
            {
                return null;
            }

            var values = new List<string>();
            foreach (var item in genres.EnumerateArray())
            {
                var value = GetJsonString(item, "description");
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value);
                }
            }

            return SelectPrimaryGenre(values);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            _genreGate.Release();
        }
    }

    /// <summary>
    /// Steam lists "Action" for almost every game, so the most descriptive
    /// genre is picked first to keep the groups meaningful.
    /// </summary>
    private static string? SelectPrimaryGenre(IReadOnlyList<string> genres)
    {
        if (genres.Count == 0)
        {
            return null;
        }

        // Tools and demo apps only carry "Free To Play"/"Early Access"; those
        // labels do not describe a genre and would create a meaningless group.
        var meaningful = genres
            .Where(genre => !string.IsNullOrWhiteSpace(genre))
            .Where(genre => !NonDescriptiveGenres.Contains(genre, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (meaningful.Count == 0)
        {
            return null;
        }

        foreach (var candidate in new[]
                 {
                     "RPG", "Simulation", "Strategy", "Racing", "Sports", "Indie",
                     "Adventure", "Massively Multiplayer", "Casual", "Action",
                 })
        {
            if (meaningful.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return meaningful[0];
    }

    private static readonly string[] NonDescriptiveGenres =
    {
        "Free To Play", "Early Access",
    };

    /// <summary>
    /// Downloads (once) and decodes a Steam portrait cover. Returns <c>null</c>
    /// when the app has no cover for this game or the machine is offline.
    /// </summary>
    private async Task<System.Windows.Media.ImageSource?> GetSteamCoverAsync(
        int appId,
        CancellationToken cancellationToken)
    {
        if (appId <= 0)
        {
            return null;
        }

        var cachePath = Path.Combine(_coverCacheDirectory, $"{appId}.jpg");

        // Older builds cached a 460x215 header for newly announced games, which
        // looks blurry stretched into a 180x260 card. Such files are replaced.
        var cacheIsUsable = File.Exists(cachePath) &&
                            new FileInfo(cachePath).Length > 0 &&
                            IsPortraitCover(cachePath);

        if (!cacheIsUsable && File.Exists(cachePath))
        {
            TryDelete(cachePath);
        }

        if (!cacheIsUsable)
        {
            var downloaded = await TryDownloadCoverAsync(appId, cachePath, cancellationToken)
                             && IsPortraitCover(cachePath);

            if (!downloaded)
            {
                TryDelete(cachePath);

                // Portrait caps from the store API; the predictable CDN paths are
                // missing for games that are announced but not released yet.
                downloaded = await TryDownloadStoreBrowseCoverAsync(
                    appId,
                    cachePath,
                    cancellationToken);
            }

            if (!downloaded)
            {
                // Last resort: any store artwork (may be a landscape header).
                downloaded = await TryDownloadAppDetailsCoverAsync(
                    appId,
                    cachePath,
                    cancellationToken);
            }

            if (!downloaded)
            {
                return null;
            }
        }

        try
        {
            return LoadFrozen(cachePath);
        }
        catch
        {
            // A corrupt cache entry should never break the library.
            try { File.Delete(cachePath); } catch { /* ignored */ }
            return null;
        }
    }

    private async Task<bool> TryDownloadCoverAsync(
        int appId,
        string cachePath,
        CancellationToken cancellationToken)
    {
        foreach (var template in CoverUrls)
        {
            try
            {
                var url = string.Format(template, appId);
                var bytes = await DownloadImageAsync(url, cancellationToken)
                    .ConfigureAwait(false);

                if (bytes is null)
                {
                    continue;
                }

                // Unique suffix: two scans may download the same cover at once.
                var tempPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
                try
                {
                    await FileEx.WriteAllBytesAsync(tempPath, bytes, cancellationToken)
                        .ConfigureAwait(false);
                    FileEx.MoveOverwrite(tempPath, cachePath);
                    return true;
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
                        // A leftover temp file is harmless.
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Try the next URL; the card falls back to its placeholder.
            }
        }

        return false;
    }

    /// <summary>
    /// Downloads a real portrait capsule (600x900 or 1200x1800) from the Steam
    /// store API. The predictable CDN paths are missing for unreleased titles,
    /// so this is the reliable source of good-quality posters.
    /// </summary>
    private async Task<bool> TryDownloadStoreBrowseCoverAsync(
        int appId,
        string cachePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = "{\"ids\":[{\"appid\":" + appId + "}],"
                        + "\"context\":{\"language\":\"english\",\"country_code\":\"US\"},"
                        + "\"data_request\":{\"include_assets\":true}}";

            var apiUrl = "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json="
                         + Uri.EscapeDataString(input);

            using var response = await GetWithRetryAsync(apiUrl, cancellationToken)
                .ConfigureAwait(false);

            if (response is null || !response.IsSuccessStatusCode)
            {
                return false;
            }

            using var stream = await response.Content
                .ReadAsStreamAsync()
                .ConfigureAwait(false);

            using var document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("response", out var payload) ||
                !payload.TryGetProperty("store_items", out var items) ||
                items.ValueKind != JsonValueKind.Array ||
                items.GetArrayLength() == 0)
            {
                return false;
            }

            var item = items[0];
            if (!item.TryGetProperty("assets", out var assets) ||
                !assets.TryGetProperty("asset_url_format", out var formatElement))
            {
                return false;
            }

            var assetFormat = formatElement.GetString();
            if (string.IsNullOrWhiteSpace(assetFormat))
            {
                return false;
            }

            foreach (var key in new[] { "library_capsule_2x", "library_capsule" })
            {
                if (!assets.TryGetProperty(key, out var fileElement))
                {
                    continue;
                }

                var fileName = fileElement.GetString();
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                var imageUrl = "https://shared.akamai.steamstatic.com/store_item_assets/"
                               + assetFormat.Replace("${FILENAME}", fileName);

                if (await TryDownloadPortraitAsync(imageUrl, cachePath, cancellationToken)
                    .ConfigureAwait(false))
                {
                    return true;
                }
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Saves the image only when it is actually a portrait poster.</summary>
    private async Task<bool> TryDownloadPortraitAsync(
        string url,
        string cachePath,
        CancellationToken cancellationToken)
    {
        if (!IsAllowedImageUrl(url))
        {
            return false;
        }

        try
        {
            using var response = await GetWithRetryAsync(url, cancellationToken)
                .ConfigureAwait(false);

            if (response is null || !response.IsSuccessStatusCode)
            {
                return false;
            }

            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength.HasValue && declaredLength.Value > MaxCoverBytes)
            {
                return false;
            }

            var bytes = await response.Content
                .ReadAsByteArrayAsync()
                .ConfigureAwait(false);

            if (bytes.Length < 512 || bytes.Length > MaxCoverBytes)
            {
                return false;
            }

            var tempPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await FileEx.WriteAllBytesAsync(tempPath, bytes, cancellationToken)
                    .ConfigureAwait(false);

                if (!IsPortraitCover(tempPath))
                {
                    return false;
                }

                FileEx.MoveOverwrite(tempPath, cachePath);
                return true;
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
                    // A leftover temp file is harmless.
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when the cached image is taller than it is wide.</summary>
    private static bool IsPortraitCover(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);

            var frame = decoder.Frames[0];

            // A decompression bomb is rejected before the image is rendered.
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 ||
                frame.PixelWidth > MaxCoverDimension ||
                frame.PixelHeight > MaxCoverDimension ||
                (long)frame.PixelWidth * frame.PixelHeight > MaxCoverPixels)
            {
                return false;
            }

            return frame.PixelHeight > frame.PixelWidth;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // The next download simply overwrites it.
        }
    }

    /// <summary>
    /// Fallback for store pages whose artwork lives under a hashed path:
    /// ask the appdetails API for the current header image URL.
    /// </summary>
    private async Task<bool> TryDownloadAppDetailsCoverAsync(
        int appId,
        string cachePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var detailsUrl = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english&cc=US";
            using var response = await GetWithRetryAsync(detailsUrl, cancellationToken)
                .ConfigureAwait(false);

            if (response is null || !response.IsSuccessStatusCode)
            {
                return false;
            }

            using var stream = await response.Content
                .ReadAsStreamAsync()
                .ConfigureAwait(false);

            using var document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty(appId.ToString(), out var entry) ||
                !entry.TryGetProperty("data", out var data))
            {
                return false;
            }

            var imageUrl = GetJsonString(data, "header_image")
                           ?? GetJsonString(data, "capsule_image");

            if (string.IsNullOrWhiteSpace(imageUrl) || !IsAllowedImageUrl(imageUrl))
            {
                return false;
            }

            using var imageResponse = await GetWithRetryAsync(imageUrl, cancellationToken)
                .ConfigureAwait(false);

            if (imageResponse is null || !imageResponse.IsSuccessStatusCode)
            {
                return false;
            }

            var declaredLength = imageResponse.Content.Headers.ContentLength;
            if (declaredLength.HasValue && declaredLength.Value > MaxCoverBytes)
            {
                return false;
            }

            var bytes = await imageResponse.Content
                .ReadAsByteArrayAsync()
                .ConfigureAwait(false);

            if (bytes.Length < 512 || bytes.Length > MaxCoverBytes)
            {
                return false;
            }

            var tempPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await FileEx.WriteAllBytesAsync(tempPath, bytes, cancellationToken)
                    .ConfigureAwait(false);
                FileEx.MoveOverwrite(tempPath, cachePath);
                return true;
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
                    // A leftover temp file is harmless.
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static System.Windows.Media.ImageSource LoadFrozen(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bitmap.DecodePixelWidth = 360; // 2x of the 180 px poster, keeps memory low
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    // ---------------------------------------------------------------------
    // Local artwork + Steam Store title lookup
    // ---------------------------------------------------------------------

    private static readonly string[] LocalCoverNames =
    {
        "cover", "poster", "boxart", "folder", "header", "thumbnail",
    };

    private static readonly string[] LocalCoverExtensions =
    {
        ".jpg", ".jpeg", ".png", ".bmp",
    };

    /// <summary>Looks for artwork shipped inside a game folder (repacks often have it).</summary>
    private static string? FindLocalCover(string folder)
    {
        try
        {
            foreach (var name in LocalCoverNames)
            {
                foreach (var extension in LocalCoverExtensions)
                {
                    var candidate = Path.Combine(folder, name + extension);
                    var info = new FileInfo(candidate);
                    if (info.Exists && info.Length > 2048)
                    {
                        return candidate;
                    }
                }
            }

            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var extension = Path.GetExtension(file);
                if (!LocalCoverExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var stem = Path.GetFileNameWithoutExtension(file);
                if (stem.IndexOf("cover", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    stem.IndexOf("poster", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    stem.IndexOf("boxart", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var info = new FileInfo(file);
                    if (info.Length > 2048)
                    {
                        return file;
                    }
                }
            }
        }
        catch
        {
            // Unreadable folder: no local artwork.
        }

        return null;
    }

    /// <summary>
    /// Finds the Steam AppID for a non-Steam game by searching the store, so
    /// folder / Epic / GOG entries can use the same portrait covers.
    /// The result is cached by the game's stable id in <c>covers/storeids.json</c>.
    /// </summary>
    private async Task<int> ResolveSteamAppIdAsync(Game game, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(game.Title) || string.IsNullOrWhiteSpace(game.Id))
        {
            return 0;
        }

        EnsureStoreIdsLoaded();

        lock (_storeIdsLock)
        {
            if (_storeIds.TryGetValue(game.Id, out var cached))
            {
                return cached;
            }

            if (_storeSearchMisses.Contains(game.Id))
            {
                return 0;
            }
        }

        await _storeSearchGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var query = BuildSearchQuery(game.Title);
            if (query.Length == 0)
            {
                return 0;
            }

            var uri = "https://store.steampowered.com/api/storesearch/?term="
                      + Uri.EscapeDataString(query)
                      + "&l=english&cc=US";

            using var response = await GetWithRetryAsync(uri, cancellationToken)
                .ConfigureAwait(false);

            if (response is null || !response.IsSuccessStatusCode)
            {
                return 0;
            }

            using var stream = await response.Content
                .ReadAsStreamAsync()
                .ConfigureAwait(false);

            using var document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var match = FindBestStoreMatch(game.Title, document.RootElement);

            lock (_storeIdsLock)
            {
                if (match > 0)
                {
                    _storeIds[game.Id] = match;
                }
                else
                {
                    _storeSearchMisses.Add(game.Id);
                }
            }

            if (match > 0)
            {
                SaveStoreIds();
            }

            return match;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Offline or a changed API: the poster placeholder stays.
            return 0;
        }
        finally
        {
            _storeSearchGate.Release();
        }
    }

    /// <summary>
    /// Forgets the Steam AppID and cached poster of a game the user removed
    /// ("Скрыть и удалить"), so adding it again resolves everything from
    /// scratch. The poster file is kept while another card uses the same AppID.
    /// </summary>
    public void ForgetGameArt(Game game, IReadOnlyCollection<Game> remainingGames)
    {
        if (game is null || string.IsNullOrWhiteSpace(game.Id))
        {
            return;
        }

        EnsureStoreIdsLoaded();

        int appId;
        bool changed;

        lock (_storeIdsLock)
        {
            appId = game.AppId > 0
                ? game.AppId
                : _storeIds.TryGetValue(game.Id, out var cached) ? cached : 0;

            changed = _storeIds.Remove(game.Id);
            changed |= _storeSearchMisses.Remove(game.Id);
        }

        if (changed)
        {
            SaveStoreIds();
        }

        if (appId <= 0)
        {
            return;
        }

        var stillUsed = remainingGames.Any(
            other => !ReferenceEquals(other, game) && other.AppId == appId);

        if (!stillUsed)
        {
            TryDelete(Path.Combine(_coverCacheDirectory, $"{appId}.jpg"));
        }
    }

    private static int FindBestStoreMatch(string title, JsonElement root)
    {
        if (!root.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var queryTokens = TokenizeTitle(title);
        if (queryTokens.Count == 0)
        {
            return 0;
        }

        var queryText = string.Concat(queryTokens);
        var bestId = 0;
        var bestScore = 0d;

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.Number ||
                !idElement.TryGetInt32(out var appId) ||
                appId <= 0 ||
                !item.TryGetProperty("name", out var nameElement))
            {
                continue;
            }

            var name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var candidateTokens = TokenizeTitle(name);
            if (candidateTokens.Count == 0)
            {
                continue;
            }

            var overlap = queryTokens.Intersect(candidateTokens, StringComparer.Ordinal).Count();
            var coverage = overlap / (double)queryTokens.Count;
            var precision = overlap / (double)candidateTokens.Count;

            // Require most of the requested words and a reasonable share of the
            // candidate's words, so sequels and DLC are not mixed up.
            if (coverage < 0.6 || precision < 0.4)
            {
                continue;
            }

            var candidateText = string.Concat(candidateTokens);
            double score;

            if (string.Equals(queryText, candidateText, StringComparison.Ordinal))
            {
                score = 1.0;
            }
            else if (candidateText.IndexOf(queryText, StringComparison.Ordinal) >= 0 ||
                     queryText.IndexOf(candidateText, StringComparison.Ordinal) >= 0)
            {
                var shorter = Math.Min(queryText.Length, candidateText.Length);
                var longer = Math.Max(queryText.Length, candidateText.Length);
                score = 0.82 + 0.12 * (shorter / (double)longer);
            }
            else
            {
                score = coverage * 0.65 + precision * 0.35;
            }

            if (IsExtraContent(name))
            {
                score -= 0.3;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestId = appId;
            }
        }

        return bestScore >= 0.65 ? bestId : 0;
    }

    private static bool IsExtraContent(string name)
    {
        var value = name.ToLowerInvariant();

        return value.Contains("soundtrack") ||
               value.Contains(" dlc") ||
               value.Contains("season pass") ||
               value.Contains("expansion") ||
               value.Contains(" - pack") ||
               value.Contains("upgrade") ||
               value.Contains("demo");
    }

    private static string BuildSearchQuery(string title)
        => string.Join(" ", TokenizeTitle(title));

    private static List<string> TokenizeTitle(string value)
    {
        var cleaned = CleanGameTitle(value)
            .Replace("'", string.Empty)
            .Replace("’", string.Empty);

        // "Lossless Scaling v3.1.0.2" -> "Lossless Scaling"
        cleaned = Regex.Replace(cleaned, @"\bv\d+(?:\.\d+)+\b", " ", RegexOptions.IgnoreCase);

        var tokens = new List<string>();

        foreach (Match match in Regex.Matches(cleaned.ToLowerInvariant(), @"[\p{L}\p{N}]+"))
        {
            var token = match.Value;

            if (Regex.IsMatch(token, @"^(19|20)\d{2}$"))
            {
                continue; // release year in a repack folder name
            }

            if (token is "remake" or "remastered")
            {
                continue; // stores usually omit this suffix
            }

            tokens.Add(token);
        }

        return tokens;
    }

    private string StoreIdsPath => Path.Combine(_coverCacheDirectory, "storeids.json");
    private void EnsureStoreIdsLoaded()
    {
        lock (_storeIdsLock)
        {
            if (_storeIdsLoaded)
            {
                return;
            }

            _storeIdsLoaded = true;

            try
            {
                var path = StoreIdsPath;
                if (!File.Exists(path))
                {
                    return;
                }

                var values = JsonSerializer.Deserialize<Dictionary<string, int>>(
                    File.ReadAllText(path));

                if (values is null)
                {
                    return;
                }

                foreach (var pair in values)
                {
                    if (pair.Value > 0)
                    {
                        _storeIds[pair.Key] = pair.Value;
                    }
                }
            }
            catch
            {
                // A corrupt cache is simply rebuilt on demand.
            }
        }
    }

    private void SaveStoreIds()
    {
        try
        {
            string json;

            lock (_storeIdsLock)
            {
                json = JsonSerializer.Serialize(
                    _storeIds,
                    new JsonSerializerOptions { WriteIndented = true });
            }

            var path = StoreIdsPath;
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, json);
            FileEx.MoveOverwrite(tempPath, path);
        }
        catch
        {
            // Best effort: the lookup is repeated next launch.
        }
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static string? ReadRegistryValue(string key, string name)
    {
        try
        {
            return Registry.GetValue(key, name, null) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadAcfValue(string content, string key)
    {
        var match = Regex.Match(content, $"\"{Regex.Escape(key)}\"\\s+\"([^\"]*)\"",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}
