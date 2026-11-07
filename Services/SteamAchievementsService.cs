using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Win32;

namespace BagaGame.Services;

public enum SteamAchievementsStatus
{
    Success,
    NoSteam,
    PrivateProfile,
    NoAppId,
    Error,
}

public sealed class SteamAchievement : INotifyPropertyChanged
{
    private double? _globalPercent;

    public string ApiName { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public bool Unlocked { get; init; }

    public string IconUrl { get; init; } = string.Empty;

    public string IconOpen { get; init; } = string.Empty;

    public string IconLocked { get; init; } = string.Empty;

    public DateTimeOffset? UnlockTime { get; init; }

    /// <summary>Share of all Steam players who unlocked this achievement.</summary>
    public double? GlobalPercent
    {
        get => _globalPercent;
        set
        {
            if (Nullable.Equals(_globalPercent, value))
            {
                return;
            }

            _globalPercent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GlobalPercentText));
        }
    }

    public string StatusText => Loc.T(Unlocked
        ? "Loc.Achievements.Unlocked"
        : "Loc.Achievements.Locked");

    public string GlobalPercentText => GlobalPercent.HasValue
        ? Loc.Format(
            "Loc.Achievements.GlobalPercent",
            GlobalPercent.Value.ToString("0.#", CultureInfo.CurrentCulture))
        : Loc.T("Loc.Achievements.GlobalPercentUnknown");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class SteamAchievementsResult
{
    public SteamAchievementsStatus Status { get; init; }

    public IReadOnlyList<SteamAchievement> Achievements { get; init; } =
        Array.Empty<SteamAchievement>();
}

/// <summary>
/// Reads achievements from the public Steam community stats page. No API key
/// is needed: the page exposes the same data as XML as long as the profile's
/// game details are public.
/// </summary>
public sealed class SteamAchievementsService
{
    private static readonly HttpClient Http = CreateClient();

    public static SteamAchievementsService Shared { get; } = new();

    /// <summary>Finds the most recently used Steam account on this PC.</summary>
    public static bool TryGetSteamUser(out string steamId, out string personaName)
    {
        steamId = string.Empty;
        personaName = string.Empty;

        try
        {
            var steamPath = GetSteamPath();
            if (steamPath is null)
            {
                return false;
            }

            var file = Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!File.Exists(file))
            {
                return false;
            }

            var text = File.ReadAllText(file);
            var accounts = Regex.Matches(
                text,
                "\"(\\d{17})\"\\s*\\{([^}]*)\\}",
                RegexOptions.Singleline);

            string? bestId = null;
            var bestName = string.Empty;
            long bestTimestamp = -1;
            var bestAutoLogin = false;

            foreach (Match account in accounts)
            {
                var id = account.Groups[1].Value;
                var body = account.Groups[2].Value;

                var timestampMatch = Regex.Match(body, "\"timestamp\"\\s*\"(\\d+)\"");
                var timestamp = timestampMatch.Success &&
                                long.TryParse(timestampMatch.Groups[1].Value, out var parsed)
                    ? parsed
                    : 0;

                var nameMatch = Regex.Match(body, "\"PersonaName\"\\s*\"([^\"]*)\"");
                var name = nameMatch.Success ? nameMatch.Groups[1].Value : string.Empty;
                var autoLogin = Regex.IsMatch(body, "\"AutoLogin\"\\s*\"1\"");

                var better = bestId is null ||
                             timestamp > bestTimestamp ||
                             (timestamp == bestTimestamp && autoLogin && !bestAutoLogin);
                if (!better)
                {
                    continue;
                }

                bestId = id;
                bestName = name;
                bestTimestamp = timestamp;
                bestAutoLogin = autoLogin;
            }

            if (bestId is null)
            {
                return false;
            }

            steamId = bestId;
            personaName = bestName;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<SteamAchievementsResult> GetAsync(
        int appId,
        CancellationToken cancellationToken = default)
    {
        if (appId <= 0)
        {
            return new SteamAchievementsResult { Status = SteamAchievementsStatus.NoAppId };
        }

        if (!TryGetSteamUser(out var steamId, out _))
        {
            return new SteamAchievementsResult { Status = SteamAchievementsStatus.NoSteam };
        }

        var url =
            $"https://steamcommunity.com/profiles/{steamId}/stats/{appId}/?xml=1";

        try
        {
            using var response = await Http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new SteamAchievementsResult { Status = SteamAchievementsStatus.Error };
            }

            var xml = await response.Content
                .ReadAsStringAsync()
                .ConfigureAwait(false);

            var document = new XmlDocument();
            document.LoadXml(xml);

            var privacy = document.GetElementsByTagName("privacyState")
                .Cast<XmlNode>()
                .FirstOrDefault()
                ?.InnerText;

            if (!string.Equals(privacy, "public", StringComparison.OrdinalIgnoreCase))
            {
                return new SteamAchievementsResult
                {
                    Status = SteamAchievementsStatus.PrivateProfile,
                };
            }

            var nodes = document.GetElementsByTagName("achievement");
            var achievements = new List<SteamAchievement>(nodes.Count);

            foreach (XmlNode node in nodes)
            {
                var unlocked = node.Attributes?["closed"]?.Value == "1";

                var unlockText = node.SelectSingleNode("unlockTimestamp")?.InnerText;
                DateTimeOffset? unlockTime = null;
                if (long.TryParse(unlockText, out var seconds) && seconds > 0)
                {
                    unlockTime = DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
                }

                achievements.Add(new SteamAchievement
                {
                    ApiName = node.SelectSingleNode("apiname")?.InnerText?.Trim() ?? string.Empty,
                    Name = node.SelectSingleNode("name")?.InnerText?.Trim() ?? string.Empty,
                    Description = node.SelectSingleNode("description")?.InnerText?.Trim() ?? string.Empty,
                    Unlocked = unlocked,
                    // Steam's community XML names the colourful icon "iconClosed"
                    // and the grey one "iconOpen" - the opposite of the state names.
                    IconOpen = node.SelectSingleNode("iconClosed")?.InnerText?.Trim() ?? string.Empty,
                    IconLocked = node.SelectSingleNode("iconOpen")?.InnerText?.Trim() ?? string.Empty,
                    IconUrl = unlocked
                        ? node.SelectSingleNode("iconClosed")?.InnerText?.Trim() ?? string.Empty
                        : node.SelectSingleNode("iconOpen")?.InnerText?.Trim() ?? string.Empty,
                    UnlockTime = unlockTime,
                });
            }

            return new SteamAchievementsResult
            {
                Status = SteamAchievementsStatus.Success,
                Achievements = achievements,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                    or TaskCanceledException
                                    or XmlException
                                    or InvalidOperationException)
        {
            return new SteamAchievementsResult { Status = SteamAchievementsStatus.Error };
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"BagaGame/{AppInfo.VersionNumber}");
        return client;
    }

    private static string? GetSteamPath()
    {
        var candidates = new List<string?>();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            candidates.Add(key?.GetValue("SteamPath") as string);

            // Some repack installers leave a fake SteamPath behind (pointing at
            // a folder inside the game). SteamExe still points at the real
            // client, so keep its folder as a separate candidate.
            var steamExe = key?.GetValue("SteamExe") as string;
            if (!string.IsNullOrWhiteSpace(steamExe))
            {
                candidates.Add(Path.GetDirectoryName(steamExe.Replace('/', '\\')));
            }
        }
        catch
        {
            // The registry is unavailable: try the machine-wide keys below.
        }

        foreach (var subKey in new[]
                 {
                     @"SOFTWARE\WOW6432Node\Valve\Steam",
                     @"SOFTWARE\Valve\Steam",
                 })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(subKey);
                candidates.Add(key?.GetValue("InstallPath") as string);
            }
            catch
            {
                // No Steam under this key.
            }
        }

        var programFilesX86 = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            candidates.Add(Path.Combine(programFilesX86, "Steam"));
        }

        var programFiles = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            candidates.Add(Path.Combine(programFiles, "Steam"));
        }

        // A repack can overwrite SteamPath with a path that does not exist.
        // Only accept a folder that really contains a Steam installation.
        foreach (var candidate in candidates)
        {
            var path = NormalizeSteamPath(candidate);
            if (path is null)
            {
                continue;
            }

            if (File.Exists(Path.Combine(path, "steam.exe")) ||
                File.Exists(Path.Combine(path, "config", "loginusers.vdf")))
            {
                return path;
            }
        }

        return null;
    }

    private static string? NormalizeSteamPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Replace('/', '\\'));
        }
        catch
        {
            return null;
        }
    }
}
