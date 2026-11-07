using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>
    /// Raised after a toast was shown. This is the proof that the game really
    /// writes achievements and that the launcher can see them.
    /// </summary>
    public event Action<Game>? AchievementShown;

    /// <summary>
    /// Raised with the API names that just became unlocked and their best-known
    /// times. The library stores them so both achievement views show a date.
    /// </summary>
    public event Action<Game, IReadOnlyDictionary<string, DateTimeOffset>>? AchievementsUnlocked;

    private readonly DispatcherTimer _timer;
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _apiByDisplayName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _apiByNumericId = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<SteamAchievement>? _catalog;
    private int _catalogAppId;
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

        Log($"start appId={appId} game={game.Title}");
        Stop();

        _game = game;
        _appId = appId;
        _primed = false;
        _known.Clear();
        _catalog = null;
        _catalogAppId = 0;
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
        _catalog = null;
        _catalogAppId = 0;
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

            if (_catalog is null || _catalogAppId != appId)
            {
                try
                {
                    _catalog = await SteamAchievementCatalogService.Shared
                        .GetAsync(
                            appId,
                            SteamAchievementCatalogService.Language,
                            _cancellation?.Token ?? CancellationToken.None)
                        .ConfigureAwait(true);
                    _catalogAppId = appId;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    _catalog = Array.Empty<SteamAchievement>();
                    _catalogAppId = appId;
                }
            }

            foreach (var achievement in _catalog)
            {
                if (string.IsNullOrWhiteSpace(achievement.ApiName))
                {
                    continue;
                }

                names[achievement.ApiName] = achievement.Name;
                _names[achievement.ApiName] = achievement.Name;
                _icons[achievement.ApiName] = achievement.IconOpen;

                if (!string.IsNullOrWhiteSpace(achievement.Name))
                {
                    _apiByDisplayName[achievement.Name] = achievement.ApiName;
                }

                var numeric = NumericSuffix(achievement.ApiName);
                if (numeric is not null)
                {
                    _apiByNumericId[numeric] = achievement.ApiName;
                }
            }

            foreach (var achievement in result.Achievements)
            {
                if (string.IsNullOrWhiteSpace(achievement.ApiName))
                {
                    continue;
                }

                if (!names.ContainsKey(achievement.ApiName))
                {
                    names[achievement.ApiName] = achievement.Name;
                    _names[achievement.ApiName] = achievement.Name;
                    _icons[achievement.ApiName] = achievement.IconOpen;
                }

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

            // Both Steam and the local files can be empty at launch: the
            // emulator only writes an achievement once one is earned. Do not
            // stop polling here, otherwise a repack without a Steam account
            // would never raise a toast for the achievements it earns later.
            if (!_primed)
            {
                foreach (var apiName in unlocked)
                {
                    _known.Add(apiName);
                }

                _primed = true;
                Log(
                    $"baseline appId={_appId} unlocked={unlocked.Count} " +
                    $"localApi={local.ApiNames.Count} localDisplay={local.DisplayNames.Count} " +
                    $"sources={string.Join(",", local.Sources)}");
                return;
            }

            var fresh = unlocked
                .Where(apiName => _known.Add(apiName))
                .ToList();

            if (fresh.Count == 0)
            {
                return;
            }

            Log(
                $"fresh appId={_appId} count={fresh.Count} " +
                $"first={fresh[0]} catalog={_catalog.Count} icons={_icons.Count}");

            NotifyUnlockTimes(game, fresh, result.Achievements);

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
            Log($"toast appId={_appId} text={text} icon={iconPath}");
            AchievementShown?.Invoke(game);
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

            NotifyUnlockTimes(_game, fresh, null);

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
            Log($"flush toast appId={_appId} text={text} icon={iconPath}");
            AchievementShown?.Invoke(_game);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void NotifyUnlockTimes(
        Game game,
        IReadOnlyList<string> fresh,
        IReadOnlyList<SteamAchievement>? steamAchievements)
    {
        if (fresh.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var times = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        foreach (var apiName in fresh)
        {
            DateTimeOffset? steamTime = null;

            if (steamAchievements is not null)
            {
                foreach (var achievement in steamAchievements)
                {
                    if (string.Equals(achievement.ApiName, apiName, StringComparison.OrdinalIgnoreCase))
                    {
                        steamTime = achievement.UnlockTime;
                        break;
                    }
                }
            }

            times[apiName] = steamTime ?? now;
        }

        AchievementsUnlocked?.Invoke(game, times);
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

        // The achievements tab already keeps icons in the shared disk cache.
        // Reuse that file first, so a toast never waits for a second download
        // or loses its artwork because the network is slow.
        var cached = AchievementIconCache.GetCached(url);
        if (!string.IsNullOrWhiteSpace(cached) && File.Exists(cached))
        {
            return cached;
        }

        try
        {
            var downloaded = await AchievementIconCache
                .GetLocalAsync(url, cancellationToken)
                .ConfigureAwait(false);

            return File.Exists(downloaded) ? downloaded : null;
        }
        catch
        {
            return null;
        }
    }

    private static void Log(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BagaGame");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "achievement-notifier.log"),
                DateTime.Now.ToString("u") + "\t" + message + Environment.NewLine);
        }
        catch
        {
            // Diagnostics never break a toast.
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
