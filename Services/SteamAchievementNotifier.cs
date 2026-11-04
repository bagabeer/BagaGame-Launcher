using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BagaGame.Models;

namespace BagaGame.Services;

/// <summary>
/// Polls Steam and the local emulator files while a game is running and raises
/// a tray balloon for every new achievement. It never overlays the game: the
/// launcher simply stays in the notification area.
/// </summary>
public sealed class SteamAchievementNotifier : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly SteamAchievementsService _steam;
    private readonly LocalAchievementsService _local;
    private readonly Action<string, string, string?> _showBalloon;
    private readonly DispatcherTimer _timer;
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _apiByDisplayName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _apiByNumericId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient IconHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
    private CancellationTokenSource? _cancellation;
    private Game? _game;
    private int _appId;
    private bool _primed;
    private bool _busy;
    private bool _disposed;

    public SteamAchievementNotifier(
        SteamAchievementsService steam,
        LocalAchievementsService local,
        Action<string, string, string?> showBalloon)
    {
        _steam = steam;
        _local = local;
        _showBalloon = showBalloon;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = PollInterval,
        };
        _timer.Tick += (_, _) => _ = PollAsync();
    }

    public void Start(Game game, int appId)
    {
        if (_disposed || appId <= 0)
        {
            return;
        }

        Stop();

        _game = game;
        _appId = appId;
        _primed = false;
        _known.Clear();
        _cancellation = new CancellationTokenSource();
        _timer.Start();

        // The first successful poll is the baseline: achievements the player
        // already had must not produce notifications.
        _ = PollAsync();
    }

    public void Stop()
    {
        _timer.Stop();
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        _game = null;
        _appId = 0;
        _primed = false;
        _known.Clear();
        _names.Clear();
        _icons.Clear();
        _apiByDisplayName.Clear();
        _apiByNumericId.Clear();
    }

    private async Task PollAsync()
    {
        if (_disposed || _busy || _appId <= 0 || _game is null)
        {
            return;
        }

        _busy = true;
        try
        {
            var appId = _appId;
            var game = _game;

            var result = await _steam
                .GetAsync(appId, _cancellation?.Token ?? CancellationToken.None)
                .ConfigureAwait(true);

            if (_appId != appId || _cancellation is null)
            {
                return;
            }

            var local = _local.Get(game, appId);
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var achievement in result.Achievements)
            {
                if (!string.IsNullOrWhiteSpace(achievement.ApiName))
                {
                    names[achievement.ApiName] = achievement.Name;
                    _names[achievement.ApiName] = achievement.Name;
                    _icons[achievement.ApiName] = achievement.IconOpen;

                    if (!string.IsNullOrWhiteSpace(achievement.Name))
                    {
                        _apiByDisplayName[achievement.Name] = achievement.ApiName;
                    }

                    var numericId = NumericSuffix(achievement.ApiName);
                    if (numericId is not null)
                    {
                        _apiByNumericId[numericId] = achievement.ApiName;
                    }
                }
            }

            var unlocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var achievement in result.Achievements.Where(item => item.Unlocked))
            {
                if (!string.IsNullOrWhiteSpace(achievement.ApiName))
                {
                    unlocked.Add(achievement.ApiName);
                }
            }

            foreach (var apiName in ResolveLocalNames(local))
            {
                unlocked.Add(apiName);
            }

            var steamUnavailable = result.Status is SteamAchievementsStatus.NoSteam
                or SteamAchievementsStatus.NoAppId;
            if (steamUnavailable &&
                local.ApiNames.Count == 0 &&
                local.DisplayNames.Count == 0)
            {
                Stop();
                return;
            }

            if (!_primed)
            {
                foreach (var apiName in unlocked)
                {
                    _known.Add(apiName);
                }

                _primed = true;
                return;
            }

            var fresh = unlocked
                .Where(apiName => _known.Add(apiName))
                .ToList();

            if (fresh.Count == 0)
            {
                return;
            }

            var displayName = names.TryGetValue(fresh[0], out var knownName) &&
                              !string.IsNullOrWhiteSpace(knownName)
                ? knownName
                : fresh[0];

            var title = Loc.T("Loc.Achievements.ToastTitle");
            var text = fresh.Count == 1
                ? displayName
                : Loc.Format("Loc.Achievements.ToastMany", displayName, fresh.Count - 1);

            var iconPath = _icons.TryGetValue(fresh[0], out var iconUrl)
                ? await TryDownloadIconAsync(iconUrl, _cancellation?.Token ?? CancellationToken.None)
                : null;

            _showBalloon(title, text, iconPath);
        }
        catch (OperationCanceledException)
        {
            // The game closed between ticks.
        }
        catch
        {
            // A failed poll is retried on the next tick.
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// One last local read when the game exits. Emulators and game saves often
    /// flush achievements at exit, after the last timer tick.
    /// </summary>
    public async Task<bool> FlushLocalAsync()
    {
        if (_disposed || !_primed || _appId <= 0 || _game is null)
        {
            return false;
        }

        try
        {
            var local = _local.Get(_game, _appId);
            var fresh = ResolveLocalNames(local)
                .Where(apiName => _known.Add(apiName))
                .ToList();

            if (fresh.Count == 0)
            {
                return false;
            }

            var displayName = _names.TryGetValue(fresh[0], out var knownName) &&
                              !string.IsNullOrWhiteSpace(knownName)
                ? knownName
                : fresh[0];

            var title = Loc.T("Loc.Achievements.ToastTitle");
            var text = fresh.Count == 1
                ? displayName
                : Loc.Format("Loc.Achievements.ToastMany", displayName, fresh.Count - 1);

            var iconPath = _icons.TryGetValue(fresh[0], out var iconUrl)
                ? await TryDownloadIconAsync(iconUrl, CancellationToken.None)
                : null;

            _showBalloon(title, text, iconPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Emulator ids and display titles are both matched to the Steam API id.
    /// FreeTP writes the English title in some builds; without this mapping a
    /// notification would show the raw title and lose the artwork.
    /// </summary>
    private IEnumerable<string> ResolveLocalNames(LocalAchievements local)
    {
        foreach (var apiName in local.ApiNames)
        {
            if (LocalAchievements.IsNumeric(apiName) &&
                _apiByNumericId.TryGetValue(apiName, out var numericMapped))
            {
                yield return numericMapped;
                continue;
            }

            yield return apiName;
        }

        foreach (var displayName in local.DisplayNames)
        {
            yield return _apiByDisplayName.TryGetValue(displayName, out var apiName)
                ? apiName
                : displayName;
        }
    }

    /// <summary>Trailing "_18" in "acobsidian_ach_18" becomes "18".</summary>
    private static string? NumericSuffix(string apiName)
    {
        var separator = apiName.LastIndexOf('_');
        if (separator < 0 || separator >= apiName.Length - 1)
        {
            return null;
        }

        var suffix = apiName.Substring(separator + 1);
        return suffix.All(char.IsDigit) ? suffix : null;
    }

    private static async Task<string?> TryDownloadIconAsync(
        string? url,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        try
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "BagaGame",
                "achievement-icons");
            Directory.CreateDirectory(directory);

            using var sha = SHA256.Create();
            var hash = BitConverter
                .ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(url)))
                .Replace("-", string.Empty);
            var path = Path.Combine(directory, hash + ".img");

            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return path;
            }

            using var response = await IconHttp
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content
                .ReadAsByteArrayAsync()
                .ConfigureAwait(false);

            if (bytes.Length < 100)
            {
                return null;
            }

            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
