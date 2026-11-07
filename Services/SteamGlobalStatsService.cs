using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BagaGame.Services;

/// <summary>
/// Reads Steam's global achievement rarity: the share of all players who
/// unlocked each achievement. The endpoint needs no API key and the values do
/// not depend on the interface language, so one cache file per game is enough.
/// </summary>
public sealed class SteamGlobalStatsService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);
    private static readonly IReadOnlyDictionary<string, double> Empty =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();
    private readonly Dictionary<int, CacheEntry> _memory = new();

    private sealed record CacheEntry(DateTime SavedUtc, IReadOnlyDictionary<string, double> Percentages);

    public static SteamGlobalStatsService Shared { get; } = new();

    public async Task<IReadOnlyDictionary<string, double>> GetAsync(
        int appId,
        CancellationToken cancellationToken)
    {
        if (appId <= 0)
        {
            return Empty;
        }

        lock (_gate)
        {
            if (_memory.TryGetValue(appId, out var memory) &&
                DateTime.UtcNow - memory.SavedUtc < Lifetime)
            {
                return memory.Percentages;
            }
        }

        var disk = LoadDisk(appId);
        if (disk is not null && DateTime.UtcNow - disk.SavedUtc < Lifetime)
        {
            lock (_gate)
            {
                _memory[appId] = disk;
            }

            return disk.Percentages;
        }

        try
        {
            var url = "https://api.steampowered.com/ISteamUserStats/" +
                      "GetGlobalAchievementPercentagesForApp/v2/?gameid=" + appId;
            using var response = await Http
                .GetAsync(url, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var percentages = Parse(json);
            if (percentages.Count == 0)
            {
                return disk?.Percentages ?? Empty;
            }

            var entry = new CacheEntry(DateTime.UtcNow, percentages);
            lock (_gate)
            {
                _memory[appId] = entry;
            }

            SaveDisk(appId, entry);
            return percentages;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A timeout or a broken response must not hide the list; just show
            // the tooltip without rarity data.
            return disk?.Percentages ?? Empty;
        }
    }

    /// <summary>
    /// Fills the rarity into the already shown tiles. The achievement model
    /// raises property changes, so the tooltip updates without rebuilding the
    /// list or resetting its scroll position.
    /// </summary>
    public static void Assign(
        IEnumerable<SteamAchievement> items,
        IReadOnlyDictionary<string, double> percentages)
    {
        if (percentages.Count == 0)
        {
            return;
        }

        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.ApiName) &&
                percentages.TryGetValue(item.ApiName, out var percent))
            {
                item.GlobalPercent = percent;
            }
        }
    }

    private static IReadOnlyDictionary<string, double> Parse(string json)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("achievementpercentages", out var root) ||
            !root.TryGetProperty("achievements", out var achievements) ||
            achievements.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in achievements.EnumerateArray())
        {
            if (!item.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name) ||
                !item.TryGetProperty("percent", out var percentElement))
            {
                continue;
            }

            var percent = percentElement.ValueKind switch
            {
                JsonValueKind.Number => percentElement.GetDouble(),
                JsonValueKind.String => double.TryParse(
                    percentElement.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsed)
                    ? parsed
                    : (double?)null,
                _ => null,
            };

            if (percent is >= 0 and <= 100)
            {
                result[name!] = percent.Value;
            }
        }

        return result;
    }

    private static string CachePath(int appId)
        => Path.Combine(
            AppPaths.DataDirectory,
            "achievements",
            appId + "-global.json");

    private static CacheEntry? LoadDisk(int appId)
    {
        try
        {
            var path = CachePath(appId);
            if (!File.Exists(path))
            {
                return null;
            }

            var items = JsonSerializer.Deserialize<Dictionary<string, double>>(
                File.ReadAllText(path));
            if (items is not { Count: > 0 })
            {
                return null;
            }

            var percentages = new Dictionary<string, double>(
                items,
                StringComparer.OrdinalIgnoreCase);
            return new CacheEntry(File.GetLastWriteTimeUtc(path), percentages);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveDisk(int appId, CacheEntry entry)
    {
        try
        {
            var path = CachePath(appId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(entry.Percentages));
        }
        catch
        {
            // The rarity cache is optional.
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BagaGame/" + AppInfo.VersionNumber);
        return client;
    }
}
