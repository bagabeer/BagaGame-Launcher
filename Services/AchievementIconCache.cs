using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BagaGame.Services;

/// <summary>
/// Downloads achievement icons once and keeps them next to the catalogue.
/// Steam's CDN is fast but every tile used to request its own image again on
/// each open; a local file makes the list appear instantly after the first run.
/// </summary>
public static class AchievementIconCache
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(12);
    private static readonly ConcurrentDictionary<string, Task<string>> InFlight =
        new(StringComparer.OrdinalIgnoreCase);

    public static Task<string> GetLocalAsync(string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !url!.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(url ?? string.Empty);
        }

        return InFlight.GetOrAdd(url, key => DownloadAsync(key, cancellationToken));
    }

    /// <summary>Returns the cached file path, or null when it still has to be downloaded.</summary>
    public static string? GetCached(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !url!.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var path = LocalPath(url);
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Warms the cache in the background so the next time the list opens the
    /// images come from disk. The current list still uses the original URLs.
    /// </summary>
    public static void Warm(IEnumerable<string?> urls)
    {
        var pending = urls
            .Where(url => !string.IsNullOrWhiteSpace(url) &&
                          url!.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (pending.Count == 0)
        {
            return;
        }

        _ = Task.Run(() => Task.WhenAll(
            pending.Select(url => GetLocalAsync(url, CancellationToken.None))));
    }

    private static async Task<string> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        var path = LocalPath(url);
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            return path;
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return path;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".part";

            using (var response = await Http
                       .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var output = File.Create(temporary);
                await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(temporary, path);
            return path;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return url;
        }
        finally
        {
            Gate.Release();
            InFlight.TryRemove(url, out _);
        }
    }

    private static string LocalPath(string url)
    {
        using var sha = SHA1.Create();
        var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(url)))
            .Replace("-", string.Empty)
            .ToLowerInvariant();

        return Path.Combine(
            AppPaths.DataDirectory,
            "achievements",
            "icons",
            hash + ".jpg");
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BagaGame/" + AppInfo.VersionNumber);
        return client;
    }
}
