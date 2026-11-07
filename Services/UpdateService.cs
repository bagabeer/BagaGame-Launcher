using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BagaGame.Services;

/// <summary>A newer release found on GitHub.</summary>
public sealed class UpdateInfo
{
    public string Version { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;
}

/// <summary>
/// Result of one lookup. "No newer release" and "the lookup failed" are kept
/// apart so a failed request is not cached as a successful check.
/// </summary>
public sealed class UpdateCheckResult
{
    public bool Success { get; set; }

    public UpdateInfo? Update { get; set; }
}

/// <summary>
/// Reads the newest stable release from the public repository with plain GET
/// requests. Nothing is downloaded and no token is used, so the check works
/// anonymously within GitHub's hourly limit.
/// </summary>
public sealed class UpdateService
{
    public const string PageUrl = "https://github.com/bagabeer/BagaGame-Launcher/releases/latest";

    private const string ApiUrl = "https://api.github.com/repos/bagabeer/BagaGame-Launcher/releases/latest";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);

    private readonly HttpClient _api;
    private readonly HttpClient _redirects;

    public UpdateService()
    {
        _api = new HttpClient { Timeout = RequestTimeout };
        _api.DefaultRequestHeaders.UserAgent.ParseAdd($"BagaGame/{AppInfo.VersionNumber}");
        _api.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        // The fallback only needs the Location header, so redirects stay manual.
        _redirects = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = RequestTimeout,
        };
        _redirects.DefaultRequestHeaders.UserAgent.ParseAdd($"BagaGame/{AppInfo.VersionNumber}");
    }

    /// <summary>
    /// Asks GitHub for the latest release. The API is the primary source; when
    /// it is unreachable or rate-limited, the version is taken from the
    /// redirect of the release page instead.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken token)
    {
        var info = await TryApiAsync(token).ConfigureAwait(false);
        if (info is null)
        {
            info = await TryRedirectAsync(token).ConfigureAwait(false);
        }

        if (info is null)
        {
            return new UpdateCheckResult { Success = false };
        }

        return new UpdateCheckResult
        {
            Success = true,
            Update = IsNewer(info.Version) ? info : null,
        };
    }

    /// <summary>True when the version is stable and newer than this build.</summary>
    public static bool IsNewer(string? candidate)
        => TryParse(candidate, out var version) && Normalize(version) > Normalize(AppInfo.Version);

    private async Task<UpdateInfo?> TryApiAsync(CancellationToken token)
    {
        try
        {
            using var response = await _api
                .GetAsync(ApiUrl, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var root = document.RootElement;

            // Drafts and pre-releases are not offered to users.
            if (GetBool(root, "draft") || GetBool(root, "prerelease"))
            {
                return null;
            }

            var tag = root.TryGetProperty("tag_name", out var tagElement)
                ? tagElement.GetString()
                : null;
            if (!TryParse(tag, out var version))
            {
                return null;
            }

            var url = root.TryGetProperty("html_url", out var urlElement)
                ? urlElement.GetString()
                : null;

            return new UpdateInfo
            {
                Version = Format(version),
                Url = string.IsNullOrWhiteSpace(url) ? PageUrl : url!,
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                    or TaskCanceledException
                                    or JsonException
                                    or InvalidOperationException)
        {
            Trace.TraceWarning($"BagaGame: update API check failed: {ex.Message}");
            return null;
        }
    }

    private async Task<UpdateInfo?> TryRedirectAsync(CancellationToken token)
    {
        try
        {
            using var response = await _redirects
                .GetAsync(PageUrl, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            var location = response.Headers.Location;
            if (location is null)
            {
                return null;
            }

            var uri = location.IsAbsoluteUri
                ? location
                : new Uri(new Uri(PageUrl), location);
            var segments = uri.Segments;
            var tag = segments.Length == 0
                ? null
                : Uri.UnescapeDataString(segments[segments.Length - 1]).Trim('/');

            if (!TryParse(tag, out var version))
            {
                return null;
            }

            return new UpdateInfo
            {
                Version = Format(version),
                Url = PageUrl,
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                    or TaskCanceledException
                                    or InvalidOperationException
                                    or UriFormatException)
        {
            Trace.TraceWarning($"BagaGame: update redirect check failed: {ex.Message}");
            return null;
        }
    }

    private static bool GetBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.True;

    /// <summary>Accepts tags such as <c>vX.Y.Z</c> and ignores suffixes.</summary>
    private static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0);

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text!.Trim().TrimStart('v', 'V');
        var suffix = value.IndexOfAny(new[] { '-', '+' });
        if (suffix >= 0)
        {
            value = value.Substring(0, suffix);
        }

        if (value.IndexOf('.') < 0)
        {
            value += ".0";
        }

        return Version.TryParse(value, out version!);
    }

    private static Version Normalize(Version version)
        => new(
            version.Major,
            version.Minor,
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0));

    /// <summary>Always three parts, so the card never shows "0.9" alone.</summary>
    private static string Format(Version version)
        => $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
}
