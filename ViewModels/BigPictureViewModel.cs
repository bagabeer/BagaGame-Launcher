using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;
using BagaGame.Models;
using BagaGame.Services;

namespace BagaGame.ViewModels;

/// <summary>
/// Data for the Big Picture window. It reuses the library owned by
/// <see cref="MainViewModel"/> and only adds television-specific state: the
/// ordered "recent" list, the selected card, the clock and the counters.
/// </summary>
public sealed class BigPictureViewModel : INotifyPropertyChanged
{
    private readonly MainViewModel _main;
    private readonly LibraryStore _store;
    private readonly DispatcherTimer _clock;
    private Game? _selectedGame;
    private bool _reloadQueued;
    private ImageSource? _heroImage;
    private string _description = string.Empty;
    private string _genres = string.Empty;

    public BigPictureViewModel(MainViewModel main, LibraryStore store)
    {
        _main = main;
        _store = store;

        Loc.Changed += OnLanguageChanged;
        _main.Games.CollectionChanged += OnGamesChanged;

        _clock = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(20),
        };
        _clock.Tick += (_, _) => OnPropertyChanged(nameof(ClockText));
        _clock.Start();

        Reload();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Games ordered by the last launch, then by title.</summary>
    public ObservableCollection<Game> Games { get; } = new();

    public Game? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (ReferenceEquals(_selectedGame, value))
            {
                return;
            }

            _selectedGame = value;

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedTitle));
            OnPropertyChanged(nameof(SelectedSubtitle));
            OnPropertyChanged(nameof(SelectedFolder));
            OnPropertyChanged(nameof(SelectedCover));
            OnPropertyChanged(nameof(SelectedInitials));
            OnPropertyChanged(nameof(SelectedPlaytimeText));
            OnPropertyChanged(nameof(CounterText));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool HasGames => Games.Count > 0;

    public bool HasSelection => _selectedGame is not null;

    public string SelectedTitle => _selectedGame?.Title ?? Loc.T("Loc.BP.Empty.Title");

    /// <summary>"Steam · 12 h" under the title.</summary>
    public string SelectedSubtitle
    {
        get
        {
            if (_selectedGame is null)
            {
                return string.Empty;
            }

            var source = Loc.SourceName(_selectedGame.Source);
            var playtime = _selectedGame.PlaytimeText;

            return string.IsNullOrWhiteSpace(playtime)
                ? source
                : string.IsNullOrWhiteSpace(source)
                    ? playtime
                    : $"{source} · {playtime}";
        }
    }

    public string SelectedFolder => _selectedGame?.InstallPath ?? string.Empty;

    public string SelectedPlaytimeText => string.IsNullOrWhiteSpace(_selectedGame?.PlaytimeText)
        ? Loc.T("Loc.BP.NotPlayed")
        : _selectedGame!.PlaytimeText;

    /// <summary>Wide banner art from Steam or SteamGridDB; null uses the poster.</summary>
    public ImageSource? HeroImage => _heroImage;

    public bool HasHero => _heroImage is not null;

    public string SelectedDescription => _description;

    public bool HasDescription => _description.Length > 0;

    public string SelectedGenres => _genres;

    public bool HasGenres => _genres.Length > 0;

    public void SetHero(ImageSource? image)
    {
        if (ReferenceEquals(_heroImage, image))
        {
            return;
        }

        _heroImage = image;
        OnPropertyChanged(nameof(HeroImage));
        OnPropertyChanged(nameof(HasHero));
    }

    public void SetMetadata(string? description, IReadOnlyList<string>? genres)
    {
        _description = string.IsNullOrWhiteSpace(description) ? string.Empty : description.Trim();
        _genres = genres is { Count: > 0 } ? string.Join("  ·  ", genres) : string.Empty;

        OnPropertyChanged(nameof(SelectedDescription));
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(SelectedGenres));
        OnPropertyChanged(nameof(HasGenres));
    }

    public ImageSource? SelectedCover => _selectedGame?.Cover;

    public string SelectedInitials => _selectedGame?.Initials ?? "?";

    public string ClockText => DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);

    public string CounterText
    {
        get
        {
            if (_selectedGame is null)
            {
                return string.Empty;
            }

            var index = Games.IndexOf(_selectedGame);
            return Loc.Format("Loc.BP.Counter", index + 1, Games.Count);
        }
    }

    public string GamesCountText => Loc.Format("Loc.Library.FoundGames", Games.Count);

    public string StatusText => _selectedGame is null
        ? Loc.T("Loc.BP.Idle")
        : Loc.Format("Loc.BP.Launch", _selectedGame.Title);

    public string EmptyTitle => Loc.T("Loc.BP.Empty.Title");

    public string EmptyText => Loc.T("Loc.BP.Empty.Text");

    /// <summary>Moves the selection; used by keyboard, mouse buttons and pads.</summary>
    public void Move(int delta)
    {
        if (Games.Count == 0)
        {
            return;
        }

        var index = _selectedGame is null ? 0 : Games.IndexOf(_selectedGame);
        if (index < 0)
        {
            index = 0;
        }

        var next = Math.Max(0, Math.Min(Games.Count - 1, index + delta));
        SelectedGame = Games[next];
    }

    /// <summary>Selects the first card when the window opens on an empty slot.</summary>
    public void EnsureSelection()
    {
        if (_selectedGame is null && Games.Count > 0)
        {
            SelectedGame = Games[0];
        }
    }

    /// <summary>Re-applies the "recent first" order, e.g. after a game was played.</summary>
    public void RefreshOrder() => Reload();

    private void OnGamesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A scan raises one event per game; coalesce them into a single rebuild.
        if (_reloadQueued)
        {
            return;
        }

        _reloadQueued = true;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
        {
            _reloadQueued = false;
            Reload();
        }), DispatcherPriority.Background);
    }

    private void Reload()
    {
        var currentId = _selectedGame?.Id;

        var ordered = _main.Games
            .OrderByDescending(game => _store.LastPlayed(game.Id))
            .ThenBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Games.Clear();
        foreach (var game in ordered)
        {
            Games.Add(game);
        }

        SelectedGame = currentId is null
            ? Games.FirstOrDefault()
            : Games.FirstOrDefault(game => string.Equals(game.Id, currentId, StringComparison.Ordinal))
              ?? Games.FirstOrDefault();

        OnPropertyChanged(nameof(HasGames));
        OnPropertyChanged(nameof(GamesCountText));
        OnPropertyChanged(nameof(CounterText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyText));
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedSubtitle));
        OnPropertyChanged(nameof(SelectedFolder));
        OnPropertyChanged(nameof(SelectedPlaytimeText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(GamesCountText));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(CounterText));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
