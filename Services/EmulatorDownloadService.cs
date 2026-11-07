using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace BagaGame.Services;

/// <summary>One build published by an emulator project.</summary>
public sealed record DownloadRelease(
    EmulatorFamily Family,
    string SourceKey,
    string SourceName,
    string Tag,
    string Label,
    string AssetName,
    string Url,
    long Size)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Tag : Label;

    public string DisplayWithSize
    {
        get
        {
            var size = Size > 0 ? " · " + (Size / 1048576d).ToString("0.0") + " MB" : string.Empty;
            return SourceName + " · " + FamilyName + size;
        }
    }

    public string FamilyName => Family switch
    {
        EmulatorFamily.Steam => "Steam",
        EmulatorFamily.Epic => "Epic (EOS)",
        _ => "GOG",
    };
}

/// <summary>
/// Downloads loaders from their official open sources: Goldberg (gbe_fork and
/// gse_fork on GitHub), Nemirtingas Epic (GitLab) and UniverseLAN (GitHub).
/// GitHub's API allows only 60 requests per hour, so the last list is cached
/// and the API is replaced by the public Atom feed plus asset HEAD checks when
/// the limit is exhausted. Archives are unpacked with tar.exe, which handles
/// both 7z and zip without extra dependencies.
/// </summary>
public static class EmulatorDownloadService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);

    private sealed record Source(
        EmulatorFamily Family,
        string Key,
        string Name,
        string Endpoint,
        string? AtomUrl,
        bool GitLab);

    private static readonly Source[] Sources =
    {
        new(EmulatorFamily.Steam, "gbe", "Goldberg (gbe_fork)",
            "https://api.github.com/repos/Detanup01/gbe_fork/releases?per_page=6",
            "https://github.com/Detanup01/gbe_fork/releases.atom", false),
        new(EmulatorFamily.Steam, "gse", "Goldberg (gse_fork)",
            "https://api.github.com/repos/alex47exe/gse_fork/releases?per_page=6",
            "https://github.com/alex47exe/gse_fork/releases.atom", false),
        new(EmulatorFamily.Epic, "nemirtingas", "Nemirtingas Epic",
            "https://gitlab.com/api/v4/projects/71162183/releases?per_page=6",
            null, true),
        new(EmulatorFamily.Gog, "universelan", "UniverseLAN",
            "https://api.github.com/repos/grasmanek94/UniverseLAN/releases?per_page=3",
            "https://github.com/grasmanek94/UniverseLAN/releases.atom", false),
    };

    public static string FileName(EmulatorFamily family) => family switch
    {
        EmulatorFamily.Epic => "EOSSDK-Win64-Shipping.dll",
        EmulatorFamily.Gog => "Galaxy64.dll",
        _ => "steam_api64.dll",
    };

    public static string BuildFolder(EmulatorFamily family, string sourceKey, string tag)
        => Path.Combine(EmulatorSwapService.FamilyRoot(family), sourceKey, SafeName(tag));

    public static bool IsCached(DownloadRelease release)
        => File.Exists(Path.Combine(
            BuildFolder(release.Family, release.SourceKey, release.Tag),
            FileName(release.Family)));

    public static async Task<IReadOnlyList<DownloadRelease>> GetReleasesAsync(
        EmulatorFamily? family,
        CancellationToken cancellationToken,
        bool force = false)
    {
        var cached = LoadCache();

        if (!force &&
            cached is not null &&
            DateTime.UtcNow - cached.SavedUtc < CacheLifetime)
        {
            return Filter(cached.Items, family);
        }

        var fetched = await FetchAllAsync(cancellationToken).ConfigureAwait(false);
        var merged = Merge(fetched, cached?.Items, family);

        if (merged.Count > 0)
        {
            SaveCache(merged);
        }

        if (merged.Count == 0 && cached is not null)
        {
            return Filter(cached.Items, family);
        }

        return Filter(merged, family);
    }

    private static IReadOnlyList<DownloadRelease> Filter(
        IEnumerable<DownloadRelease> items,
        EmulatorFamily? family)
        => (family is null ? items : items.Where(item => item.Family == family.Value))
            .OrderBy(item => item.Family)
            .ThenBy(item => item.SourceName, StringComparer.CurrentCultureIgnoreCase)
            .ThenByDescending(item => item.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static async Task<List<DownloadRelease>> FetchAllAsync(CancellationToken cancellationToken)
    {
        var tasks = Sources
            .Select(source => LoadSourceAsync(source, cancellationToken))
            .ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.SelectMany(items => items).ToList();
    }

    private static async Task<IReadOnlyList<DownloadRelease>> LoadSourceAsync(
        Source source,
        CancellationToken cancellationToken)
    {
        var fromApi = await LoadApiAsync(source, cancellationToken).ConfigureAwait(false);
        if (fromApi.Count > 0 || source.AtomUrl is null)
        {
            return fromApi;
        }

        return await LoadFallbackAsync(source, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<DownloadRelease>> LoadApiAsync(
        Source source,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source.Endpoint);
            request.Headers.Accept.ParseAdd(source.GitLab ? "application/json" : "application/vnd.github+json");

            using var response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            return source.GitLab
                ? ParseGitLab(source, document.RootElement)
                : ParseGitHub(source, document.RootElement);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Array.Empty<DownloadRelease>();
        }
    }

    // ------------------------------------------------------------------
    // GitHub fallback without the API limit
    // ------------------------------------------------------------------

    private sealed record AtomEntry(string Tag, string Label);

    private static async Task<IReadOnlyList<DownloadRelease>> LoadFallbackAsync(
        Source source,
        CancellationToken cancellationToken)
    {
        try
        {
            var atom = await Http.GetStringAsync(source.AtomUrl!).ConfigureAwait(false);
            var document = XDocument.Parse(atom);
            XNamespace ns = "http://www.w3.org/2005/Atom";

            var tags = document.Root?
                .Elements(ns + "entry")
                .Select(entry =>
                {
                    var link = entry.Element(ns + "link")?.Attribute("href")?.Value ?? string.Empty;
                    var tag = link.Substring(link.LastIndexOf('/') + 1);
                    return new AtomEntry(
                        tag,
                        entry.Element(ns + "title")?.Value ?? tag);
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Tag))
                .Take(6)
                .ToList() ?? new List<AtomEntry>();

            if (tags.Count == 0)
            {
                return Array.Empty<DownloadRelease>();
            }

            if (source.Key == "universelan")
            {
                return await LoadUniverseLanAssetsAsync(source, tags, cancellationToken)
                    .ConfigureAwait(false);
            }

            return await LoadGoldbergAssetsAsync(source, tags, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Array.Empty<DownloadRelease>();
        }
    }

    private static async Task<IReadOnlyList<DownloadRelease>> LoadGoldbergAssetsAsync(
        Source source,
        IEnumerable<AtomEntry> tags,
        CancellationToken cancellationToken)
    {
        var candidates = source.Key == "gse"
            ? new[] { "emu-win-release.7z", "emu-win-release-vs22.7z" }
            : new[] { "emu-win-release-vs22.7z", "emu-win-release.7z", "emu-win-release-vs26.7z" };

        var results = new List<DownloadRelease>();
        var repository = source.AtomUrl!
            .Replace("https://github.com/", string.Empty)
            .Replace("/releases.atom", string.Empty);

        foreach (var tag in tags)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var asset in candidates)
            {
                var url = "https://github.com/" + repository + "/releases/download/" +
                          Uri.EscapeDataString(tag.Tag) + "/" + asset;

                if (!await ExistsAsync(url, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                results.Add(new DownloadRelease(
                    source.Family,
                    source.Key,
                    source.Name,
                    tag.Tag,
                    tag.Label,
                    asset,
                    url,
                    0));
                break;
            }
        }

        return results;
    }

    private static async Task<IReadOnlyList<DownloadRelease>> LoadUniverseLanAssetsAsync(
        Source source,
        IEnumerable<AtomEntry> tags,
        CancellationToken cancellationToken)
    {
        var results = new List<DownloadRelease>();
        var pattern = new Regex(
            "UniverseLAN-([0-9.]+)-Build-[0-9]+-x64_x86\\.zip",
            RegexOptions.IgnoreCase);

        foreach (var tag in tags)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = "https://github.com/grasmanek94/UniverseLAN/releases/expanded_assets/" +
                      Uri.EscapeDataString(tag.Tag);
            var html = await Http.GetStringAsync(url).ConfigureAwait(false);

            foreach (Match match in pattern.Matches(html))
            {
                var asset = match.Value;
                var version = match.Groups[1].Value;
                results.Add(new DownloadRelease(
                    source.Family,
                    source.Key,
                    source.Name,
                    version,
                    "UniverseLAN " + version,
                    asset,
                    "https://github.com/grasmanek94/UniverseLAN/releases/download/" +
                    Uri.EscapeDataString(tag.Tag) + "/" + asset,
                    0));
            }
        }

        return results
            .GroupBy(item => item.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(item => ParseVersion(item.Tag))
            .Take(8)
            .ToList();
    }

    private static async Task<bool> ExistsAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // API parsers
    // ------------------------------------------------------------------

    private static IReadOnlyList<DownloadRelease> ParseGitHub(Source source, JsonElement root)
    {
        var releases = new List<DownloadRelease>();
        if (root.ValueKind != JsonValueKind.Array)
        {
            return releases;
        }

        foreach (var release in root.EnumerateArray())
        {
            var tag = GetString(release, "tag_name");
            if (string.IsNullOrWhiteSpace(tag) || !release.TryGetProperty("assets", out var assets))
            {
                continue;
            }

            var label = GetString(release, "name") ?? tag!;

            if (source.Key == "universelan" && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = GetString(asset, "name");
                    if (name is null ||
                        !name.StartsWith("UniverseLAN-", StringComparison.OrdinalIgnoreCase) ||
                        !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var version = name.Substring("UniverseLAN-".Length).Split('-')[0];
                    releases.Add(new DownloadRelease(
                        source.Family,
                        source.Key,
                        source.Name,
                        version,
                        "UniverseLAN " + version,
                        name,
                        GetString(asset, "browser_download_url") ?? string.Empty,
                        asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0));
                }

                continue;
            }

            var chosen = PickGithubAsset(assets, out var assetName);
            if (chosen is null)
            {
                continue;
            }

            releases.Add(new DownloadRelease(
                source.Family,
                source.Key,
                source.Name,
                tag!,
                label!,
                assetName!,
                GetString(chosen.Value, "browser_download_url") ?? string.Empty,
                chosen.Value.TryGetProperty("size", out var chosenSize) ? chosenSize.GetInt64() : 0));
        }

        if (source.Key == "universelan")
        {
            return releases
                .GroupBy(item => item.Tag, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(item => ParseVersion(item.Tag))
                .Take(8)
                .ToList();
        }

        return releases;
    }

    private static IReadOnlyList<DownloadRelease> ParseGitLab(Source source, JsonElement root)
    {
        var releases = new List<DownloadRelease>();
        if (root.ValueKind != JsonValueKind.Array)
        {
            return releases;
        }

        foreach (var release in root.EnumerateArray())
        {
            var tag = GetString(release, "tag_name");
            if (string.IsNullOrWhiteSpace(tag) ||
                !release.TryGetProperty("assets", out var assets) ||
                !assets.TryGetProperty("links", out var links) ||
                links.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var url = string.Empty;
            var name = string.Empty;

            foreach (var link in links.EnumerateArray())
            {
                var linkName = GetString(link, "name") ?? string.Empty;
                if (linkName.Equals("EOSSDK-Win64-Shipping.dll_no_network", StringComparison.OrdinalIgnoreCase) ||
                    linkName.Equals("EOSSDK-Win64-Shipping.dll", StringComparison.OrdinalIgnoreCase))
                {
                    name = linkName;
                    url = GetString(link, "direct_asset_url") ?? GetString(link, "url") ?? string.Empty;

                    if (linkName.EndsWith("_no_network", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            releases.Add(new DownloadRelease(
                source.Family,
                source.Key,
                source.Name,
                tag!,
                "Nemirtingas " + tag,
                name,
                url,
                0));
        }

        return releases;
    }

    private static JsonElement? PickGithubAsset(JsonElement assets, out string? name)
    {
        name = null;
        if (assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement? fallback = null;
        var fallbackName = string.Empty;

        foreach (var asset in assets.EnumerateArray())
        {
            var assetName = GetString(asset, "name");
            if (string.IsNullOrWhiteSpace(assetName) ||
                !assetName!.StartsWith("emu-win-release", StringComparison.OrdinalIgnoreCase) ||
                !assetName.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (assetName.IndexOf("vs22", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                name = assetName;
                return asset;
            }

            if (fallback is null)
            {
                fallback = asset;
                fallbackName = assetName;
            }
        }

        name = fallbackName;
        return fallback;
    }

    // ------------------------------------------------------------------
    // Cache
    // ------------------------------------------------------------------

    private sealed class ReleaseCacheFile
    {
        public DateTime SavedUtc { get; set; }

        public List<DownloadRelease> Items { get; set; } = new();
    }

    private static string CachePath
        => Path.Combine(EmulatorSwapService.CacheRoot, "releases.json");

    private static ReleaseCacheFile? LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return null;
            }

            var cache = JsonSerializer.Deserialize<ReleaseCacheFile>(
                File.ReadAllText(CachePath));

            return cache is { Items.Count: > 0 } ? cache : null;
        }
        catch
        {
            return null;
        }
    }

    private static List<DownloadRelease> Merge(
        IReadOnlyList<DownloadRelease> fetched,
        IReadOnlyList<DownloadRelease>? cached,
        EmulatorFamily? family)
    {
        var result = new List<DownloadRelease>();
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in fetched)
        {
            sources.Add(item.SourceKey);
            result.Add(item);
        }

        if (cached is not null)
        {
            var cachedSources = new HashSet<string>(
                cached.Select(item => item.SourceKey),
                StringComparer.OrdinalIgnoreCase);

            foreach (var missing in cachedSources.Where(key => !sources.Contains(key)))
            {
                result.AddRange(cached.Where(item => item.SourceKey == missing));
            }
        }

        return result;
    }

    private static void SaveCache(IReadOnlyList<DownloadRelease> items)
    {
        try
        {
            var cache = new ReleaseCacheFile
            {
                SavedUtc = DateTime.UtcNow,
                Items = items.ToList(),
            };

            Directory.CreateDirectory(EmulatorSwapService.CacheRoot);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(cache));
        }
        catch
        {
            // A read-only folder only loses the offline copy.
        }
    }

    // ------------------------------------------------------------------
    // Download / unpack
    // ------------------------------------------------------------------

    /// <summary>Returns the cached loader file, downloading and unpacking on first use.</summary>
    public static async Task<string> EnsureAsync(
        DownloadRelease release,
        IProgress<double>? progress,
        IProgress<string>? stage,
        CancellationToken cancellationToken)
    {
        var folder = BuildFolder(release.Family, release.SourceKey, release.Tag);
        var loader = Path.Combine(folder, FileName(release.Family));
        if (File.Exists(loader))
        {
            return loader;
        }

        Directory.CreateDirectory(folder);

        var cache = Path.Combine(EmulatorSwapService.CacheRoot, "_cache");
        Directory.CreateDirectory(cache);

        stage?.Report("download");

        if (release.Family == EmulatorFamily.Epic)
        {
            // GitLab serves the DLL directly, no archive to unpack.
            await DownloadAsync(release.Url, loader, progress, cancellationToken).ConfigureAwait(false);
            return loader;
        }

        var extension = release.AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ".zip" : ".7z";
        var archive = Path.Combine(cache, release.SourceKey + "-" + SafeName(release.Tag) + extension);

        if (!File.Exists(archive) || new FileInfo(archive).Length == 0)
        {
            await DownloadAsync(release.Url, archive, progress, cancellationToken).ConfigureAwait(false);
        }

        stage?.Report("extract");
        var temp = Path.Combine(folder, "_extract");
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, true);
        }

        Directory.CreateDirectory(temp);
        try
        {
            Extract(archive, temp);

            var found = Directory.GetFiles(temp, FileName(release.Family), SearchOption.AllDirectories);
            if (found.Length == 0)
            {
                throw new InvalidOperationException("В архиве нет " + FileName(release.Family) + ".");
            }

            File.Copy(found[0], loader, true);

            if (release.Family == EmulatorFamily.Gog)
            {
                var config = Directory.GetFiles(temp, "UniverseLAN.ini", SearchOption.AllDirectories);
                if (config.Length > 0)
                {
                    File.Copy(config[0], Path.Combine(folder, "UniverseLAN.ini"), true);
                }
            }

            // The archive is no longer needed once the loader is cached.
            try
            {
                if (File.Exists(archive))
                {
                    File.Delete(archive);
                }
            }
            catch
            {
                // The cleanup pass removes it later.
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(temp))
                {
                    Directory.Delete(temp, true);
                }
            }
            catch
            {
                // The cache folder is disposable.
            }
        }

        return loader;
    }

    private static async Task DownloadAsync(
        string url,
        string destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await Http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var output = File.Create(destination);

        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
            written += read;

            if (total > 0)
            {
                progress?.Report(Math.Min(100d, written * 100d / total));
            }
        }
    }

    private static void Extract(string archive, string destination)
    {
        var tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
        if (!File.Exists(tar))
        {
            tar = "tar.exe";
        }

        var info = new ProcessStartInfo
        {
            FileName = tar,
            Arguments = "-xf \"" + archive + "\" -C \"" + destination + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        using var process = Process.Start(info) ??
                            throw new InvalidOperationException("Не удалось запустить tar.exe.");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Не удалось распаковать архив" +
                (string.IsNullOrWhiteSpace(error) ? "." : ": " + error.Trim()));
        }
    }

    private static double ParseVersion(string value)
    {
        var parts = value.Split('.');
        double result = 0;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var number))
            {
                break;
            }

            result = result * 1000 + number;
        }

        return result;
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        return builder.ToString();
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BagaGame/" + AppInfo.VersionNumber);
        return client;
    }
}
