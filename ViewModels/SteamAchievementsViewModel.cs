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
        OnPropertyChanged(nameof(HasAchievements));

        try
        {
            var result = await _service.GetAsync(_appId, cancellationToken);
            var local = _local.Get(_game, _appId);

            Achievements.Clear();
            foreach (var achievement in result.Achievements
                         .Select(item => Merge(item, local))
                         .OrderByDescending(item => item.Unlocked))
            {
                Achievements.Add(achievement);
            }

            _unlocked = Achievements.Count(item => item.Unlocked);
            ProgressText = result.Status == SteamAchievementsStatus.Success
                ? Loc.Format("Loc.Achievements.Progress", _unlocked, Achievements.Count)
                : string.Empty;

            SourceText = local.Sources.Count > 0
                ? Loc.Format("Loc.Achievements.LocalSource", string.Join(", ", local.Sources))
                : string.Empty;

            StatusText = result.Status switch
            {
                SteamAchievementsStatus.Success when Achievements.Count == 0 =>
                    Loc.T("Loc.Achievements.None"),
                SteamAchievementsStatus.Success => string.Empty,
                SteamAchievementsStatus.NoSteam => Loc.T("Loc.Achievements.NoSteam"),
                SteamAchievementsStatus.PrivateProfile => Loc.T("Loc.Achievements.Private"),
                SteamAchievementsStatus.NoAppId => Loc.T("Loc.Achievements.NoAppId"),
                _ => Loc.T("Loc.Achievements.Error"),
            };
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
            OnPropertyChanged(nameof(UnlockedCount));
            OnPropertyChanged(nameof(HasAchievements));
        }
    }

    private static SteamAchievement Merge(SteamAchievement achievement, LocalAchievements local)
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
