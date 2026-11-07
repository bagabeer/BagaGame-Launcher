using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BagaGame.Models;
using BagaGame.Services;

namespace BagaGame.ViewModels;

public sealed class SteamAchievementsViewModel : INotifyPropertyChanged
{
    private readonly SteamAchievementsService _service;
    private readonly LocalAchievementsService _local;
    private readonly Game _game;
    private readonly int _appId;
    private string _progressText = string.Empty;
    private string _statusText = string.Empty;
    private string _sourceText = string.Empty;
    private bool _isLoading;
    private int _unlocked;

    public SteamAchievementsViewModel(
        Game game,
        int appId,
        SteamAchievementsService service,
        LocalAchievementsService local)
    {
        _game = game;
        _appId = appId;
        _service = service;
        _local = local;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string GameTitle => _game.Title;

    public ObservableCollection<SteamAchievement> Achievements { get; } = new();

    public int Total => Achievements.Count;

    /// <summary>Never zero, so the progress bar has a valid range.</summary>
    public int ProgressMaximum => Total > 0 ? Total : 1;

    public int UnlockedCount => _unlocked;

    public string ProgressText
    {
        get => _progressText;
        private set => SetField(ref _progressText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string SourceText
    {
        get => _sourceText;
        private set
        {
            if (_sourceText == value)
            {
                return;
            }

            _sourceText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasLocalSource));
        }
    }

    public bool HasLocalSource => !string.IsNullOrWhiteSpace(SourceText);

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    public bool HasAchievements => Achievements.Count > 0;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        StatusText = Loc.T("Loc.Achievements.Loading");
        SourceText = string.Empty;
        Achievements.Clear();
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(ProgressMaximum));
        OnPropertyChanged(nameof(HasAchievements));

        try
        {
            // Start rarity loading immediately; it is a separate Steam endpoint.
            var percentagesTask = SteamGlobalStatsService.Shared
                .GetAsync(_appId, cancellationToken);

            var result = await _service.GetAsync(_appId, cancellationToken);
            var local = _local.Get(_game, _appId);

            var catalog = await SteamAchievementCatalogService.Shared
                .GetAsync(_appId, SteamAchievementCatalogService.Language, cancellationToken)
                .ConfigureAwait(true);

            var byApi = result.Achievements
                .Where(item => !string.IsNullOrWhiteSpace(item.ApiName))
                .GroupBy(item => item.ApiName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var hasCatalog = catalog.Count > 0;
            var source = hasCatalog ? catalog : result.Achievements;
            var merged = new List<SteamAchievement>();

            foreach (var item in source)
            {
                var baseItem = item;

                if (hasCatalog && byApi.TryGetValue(item.ApiName, out var steam))
                {
                    baseItem = new SteamAchievement
                    {
                        ApiName = item.ApiName,
                        Name = item.Name,
                        Description = item.Description,
                        Unlocked = steam.Unlocked,
                        IconOpen = item.IconOpen,
                        IconLocked = item.IconLocked,
                        IconUrl = steam.Unlocked && !string.IsNullOrWhiteSpace(item.IconOpen)
                            ? item.IconOpen
                            : item.IconLocked,
                        UnlockTime = steam.UnlockTime,
                    };
                }

                merged.Add(Merge(baseItem, local));
            }

            Achievements.Clear();
            foreach (var achievement in merged.OrderByDescending(item => item.Unlocked))
            {
                Achievements.Add(achievement);
            }

            _unlocked = Achievements.Count(item => item.Unlocked);
            ProgressText = result.Status == SteamAchievementsStatus.Success || hasCatalog
                ? Loc.Format("Loc.Achievements.Progress", _unlocked, Achievements.Count)
                : string.Empty;

            SourceText = local.Sources.Count > 0
                ? Loc.Format("Loc.Achievements.LocalSource", string.Join(", ", local.Sources))
                : string.Empty;

            StatusText = Achievements.Count > 0
                ? result.Status switch
                {
                    SteamAchievementsStatus.NoSteam => Loc.T("Loc.Achievements.NoSteamNote"),
                    SteamAchievementsStatus.PrivateProfile => Loc.T("Loc.Achievements.PrivateNote"),
                    SteamAchievementsStatus.Error => Loc.T("Loc.Achievements.ErrorNote"),
                    _ => string.Empty,
                }
                : result.Status switch
                {
                    SteamAchievementsStatus.Success => Loc.T("Loc.Achievements.None"),
                    SteamAchievementsStatus.NoSteam => Loc.T("Loc.Achievements.NoSteam"),
                    SteamAchievementsStatus.PrivateProfile => Loc.T("Loc.Achievements.Private"),
                    SteamAchievementsStatus.NoAppId => Loc.T("Loc.Achievements.NoAppId"),
                    _ => Loc.T("Loc.Achievements.Error"),
                };

            // The window is already filled; rarity arrives in the background.
            _ = AssignPercentagesAsync(percentagesTask, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The window closed while loading.
        }
        catch
        {
            StatusText = Loc.T("Loc.Achievements.Error");
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(ProgressMaximum));
            OnPropertyChanged(nameof(UnlockedCount));
            OnPropertyChanged(nameof(HasAchievements));
        }
    }

    private async Task AssignPercentagesAsync(
        Task<IReadOnlyDictionary<string, double>> percentagesTask,
        CancellationToken cancellationToken)
    {
        try
        {
            var percentages = await percentagesTask.ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            SteamGlobalStatsService.Assign(Achievements, percentages);
        }
        catch (OperationCanceledException)
        {
            // The window closed while loading.
        }
        catch
        {
            // Rarity is optional; the tiles keep the "no data" tooltip.
        }
    }

    internal static SteamAchievement Merge(SteamAchievement achievement, LocalAchievements local)
    {
        if (achievement.Unlocked ||
            string.IsNullOrWhiteSpace(achievement.ApiName) ||
            !local.IsUnlocked(achievement.ApiName, achievement.Name))
        {
            return achievement;
        }

        return new SteamAchievement
        {
            ApiName = achievement.ApiName,
            Name = achievement.Name,
            Description = achievement.Description,
            Unlocked = true,
            IconOpen = achievement.IconOpen,
            IconLocked = achievement.IconLocked,
            IconUrl = string.IsNullOrWhiteSpace(achievement.IconOpen)
                ? achievement.IconUrl
                : achievement.IconOpen,
            UnlockTime = achievement.UnlockTime,
        };
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void SetField(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        OnPropertyChanged(name);
    }

    private void SetField(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        OnPropertyChanged(name);
    }
}
