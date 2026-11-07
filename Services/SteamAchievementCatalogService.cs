using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BagaGame.Services;

/// <summary>
/// Reads the achievement catalogue from Steam's public stats page in the
/// launcher language. The page embeds the localised names, descriptions and
/// icons, which the profile XML does not: it always answers in English.
/// Works without an API key and without owning the game on Steam.
/// </summary>
public sealed class SteamAchievementCatalogService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly object Gate = new();
    private static readonly Dictionary<string, CacheEntry> Memory = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    private sealed record CacheEntry(DateTime SavedUtc, IReadOnlyList<SteamAchievement> Items);

    public static SteamAchievementCatalogService Shared { get; } = new();

    public static string Language => Loc.Current == AppLanguage.Russian ? "russian" : "english";

    public async Task<IReadOnlyList<SteamAchievement>> GetAsync(
        int appId,
        string language,
        CancellationToken cancellationToken)
    {
        if (appId <= 0)
        {
            return Array.Empty<SteamAchievement>();
        }

        var key = appId + "|" + language;

        lock (Gate)
        {
            if (Memory.TryGetValue(key, out var memory) &&
                DateTime.UtcNow - memory.SavedUtc < Lifetime)
            {
                var localized = LocalizeCached(memory.Items);
                if (!ReferenceEquals(localized, memory.Items))
                {
                    Memory[key] = new CacheEntry(memory.SavedUtc, localized);
                }

                return localized;
            }
        }

        var disk = LoadDisk(appId, language);
        if (disk is not null && DateTime.UtcNow - disk.SavedUtc < Lifetime)
        {
            var localized = LocalizeCached(disk.Items);
            Remember(key, new CacheEntry(disk.SavedUtc, localized));
            WarmIcons(localized);
            return localized;
        }

        try
        {
            var url = "https://steamcommunity.com/stats/" + appId + "/achievements/";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation(
                "Accept-Language",
                language == "russian"
                    ? "ru-RU,ru;q=0.9,en;q=0.8"
                    : "en-US,en;q=0.9");
            request.Headers.TryAddWithoutValidation("Cookie", "Steam_Language=" + language);

            using var response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var items = Parse(html, appId);
            if (items.Count == 0)
            {
                return disk?.Items ?? Array.Empty<SteamAchievement>();
            }

            items = LocalizeCached(items);
            WarmIcons(items);

            var entry = new CacheEntry(DateTime.UtcNow, items);
            Remember(key, entry);
            SaveDisk(appId, language, entry);
            return items;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return disk?.Items ?? Array.Empty<SteamAchievement>();
        }
    }

    private static IReadOnlyList<SteamAchievement> Parse(string html, int appId)
    {
        var text = Unescape(html);
        const string marker = "\"achievements\":[";
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return Array.Empty<SteamAchievement>();
        }

        var start = index + marker.Length - 1;
        var end = FindArrayEnd(text, start);
        if (end < 0)
        {
            return Array.Empty<SteamAchievement>();
        }

        using var document = JsonDocument.Parse(text.Substring(start, end - start + 1));
        var items = new List<SteamAchievement>();

        foreach (var item in document.RootElement.EnumerateArray())
        {
            var api = GetString(item, "api_name");
            if (string.IsNullOrWhiteSpace(api))
            {
                continue;
            }

            var name = GetString(item, "name");
            var description = GetString(item, "description");
            var achieved = GetString(item, "icon_achieved");
            var unachieved = GetString(item, "icon_unachieved");

            items.Add(new SteamAchievement
            {
                ApiName = api!,
                Name = string.IsNullOrWhiteSpace(name) ? api! : name!,
                Description = description ?? string.Empty,
                IconOpen = IconUrl(appId, achieved),
                IconLocked = IconUrl(appId, unachieved),
                IconUrl = IconUrl(appId, unachieved),
            });
        }

        return items;
    }

    private static IReadOnlyList<SteamAchievement> LocalizeCached(IReadOnlyList<SteamAchievement> items)
    {
        var result = new List<SteamAchievement>(items.Count);

        foreach (var item in items)
        {
            var open = AchievementIconCache.GetCached(item.IconOpen) ?? item.IconOpen;
            var locked = AchievementIconCache.GetCached(item.IconLocked) ?? item.IconLocked;
            var shown = AchievementIconCache.GetCached(item.IconUrl) ?? item.IconUrl;

            if (string.Equals(open, item.IconOpen, StringComparison.Ordinal) &&
                string.Equals(locked, item.IconLocked, StringComparison.Ordinal) &&
                string.Equals(shown, item.IconUrl, StringComparison.Ordinal))
            {
                result.Add(item);
                continue;
            }

            result.Add(new SteamAchievement
            {
                ApiName = item.ApiName,
                Name = item.Name,
                Description = item.Description,
                Unlocked = item.Unlocked,
                IconOpen = open,
                IconLocked = locked,
                IconUrl = shown,
                UnlockTime = item.UnlockTime,
            });
        }

        return result;
    }

    private static void WarmIcons(IReadOnlyList<SteamAchievement> items)
    {
        var urls = new List<string?>(items.Count * 2);
        foreach (var item in items)
        {
            urls.Add(item.IconOpen);
            urls.Add(item.IconLocked);
        }

        AchievementIconCache.Warm(urls);
    }

    /// <summary>
    /// Decodes the JavaScript string escaping used by Steam's embedded JSON in
    /// one pass. Unescaping only "\"" would corrupt inner JSON escapes such as
    /// \\\" and break descriptions that contain quotation marks.
    /// </summary>
    private static string Unescape(string text)
    {
        var builder = new StringBuilder(text.Length);

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character != '\\' || index + 1 >= text.Length)
            {
                builder.Append(character);
                continue;
            }

            var next = text[++index];
            switch (next)
            {
                case '"':
                    builder.Append('"');
                    break;
                case '\\':
                    builder.Append('\\');
                    break;
                case '/':
                    builder.Append('/');
                    break;
                case 'b':
                    builder.Append('\b');
                    break;
                case 'f':
                    builder.Append('\f');
                    break;
                case 'n':
                    builder.Append('\n');
                    break;
                case 'r':
                    builder.Append('\r');
                    break;
                case 't':
                    builder.Append('\t');
                    break;
                case 'u':
                    if (index + 4 < text.Length &&
                        int.TryParse(
                            text.Substring(index + 1, 4),
                            NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture,
                            out var code))
                    {
                        builder.Append((char)code);
                        index += 4;
                    }
                    else
                    {
                        builder.Append("\\u");
                    }

                    break;
                default:
                    builder.Append(next);
                    break;
            }
        }

        return builder.ToString();
    }

    private static int FindArrayEnd(string text, int start)    {
        var depth = 0;
        var inString = false;
        var escape = false;

        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];

            if (escape)
            {
                escape = false;
                continue;
            }

            if (character == '\\')
            {
                escape = true;
                continue;
            }

            if (character == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (character == '[' || character == '{')
            {
                depth++;
            }
            else if (character == ']' || character == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        return -1;
    }

    private static string IconUrl(int appId, string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            return string.Empty;
        }

        return "https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/" +
               appId + "/" + icon;
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void Remember(string key, CacheEntry entry)
    {
        lock (Gate)
        {
            Memory[key] = entry;
        }
    }

    private static string CachePath(int appId, string language)
        => Path.Combine(
            AppPaths.DataDirectory,
            "achievements",
            appId + "-" + language + ".json");

    private static CacheEntry? LoadDisk(int appId, string language)
    {
        try
        {
            var path = CachePath(appId, language);
            if (!File.Exists(path))
            {
                return null;
            }

            var items = JsonSerializer.Deserialize<List<SteamAchievement>>(File.ReadAllText(path));
            return items is { Count: > 0 }
                ? new CacheEntry(File.GetLastWriteTimeUtc(path), items)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveDisk(int appId, string language, CacheEntry entry)
    {
        try
        {
            var path = CachePath(appId, language);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(entry.Items));
        }
        catch
        {
            // The cache is optional.
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BagaGame/" + AppInfo.VersionNumber);
        return client;
    }
}
