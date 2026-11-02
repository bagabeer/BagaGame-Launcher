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
    private bool _isGridMode;
    private LibraryGrouping _grouping = LibraryGrouping.None;
    private bool _favoritesOnly;
    private GenreFilter? _selectedGenreFilter;
    private bool _buildingGenreFilters;
    private bool _reloadQueued;
    private ImageSource? _heroImage;
    private string _description = string.Empty;
    private string _details = string.Empty;
    private string _genres = string.Empty;

    public BigPictureViewModel(MainViewModel main, LibraryStore store)
    {
        _main = main;
        _store = store;

        Loc.Changed += OnLanguageChanged;
        _main.Games.CollectionChanged += OnGamesChanged;
        _main.GenresUpdated += OnGenresUpdated;

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
            OnPropertyChanged(nameof(HasPlaytime));
            OnPropertyChanged(nameof(PlaytimePillText));
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

    public bool HasPlaytime => _selectedGame?.HasPlaytime == true;

    public string PlaytimePillText => _selectedGame is null
        ? string.Empty
        : Loc.Format("Loc.BP.PlayedFor", _selectedGame.PlaytimeText);

    /// <summary>Wide banner art from Steam or SteamGridDB; null uses the poster.</summary>
    public ImageSource? HeroImage => _heroImage;

    public bool HasHero => _heroImage is not null;

    public string SelectedDescription => _description;

    public bool HasDescription => _description.Length > 0;

    public string SelectedGenres => _genres;

    public bool HasGenres => _genres.Length > 0;

    /// <summary>Full store description for the details panel.</summary>
    public string SelectedDetails => _details;

    public bool HasDetails => _details.Length > 0;

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

    public void SetMetadata(string? description, string? details, IReadOnlyList<string>? genres)
    {
        _description = string.IsNullOrWhiteSpace(description) ? string.Empty : description.Trim();
        _details = string.IsNullOrWhiteSpace(details) ? string.Empty : details.Trim();
        _genres = genres is { Count: > 0 } ? string.Join("  ·  ", genres) : string.Empty;

        OnPropertyChanged(nameof(SelectedDescription));
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(SelectedDetails));
        OnPropertyChanged(nameof(HasDetails));
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

    /// <summary>Genre chips for the filter bar; only filled for the Genre grouping.</summary>
    public ObservableCollection<GenreFilter> GenreFilters { get; } = new();

    public bool IsGenreGrouping => !_favoritesOnly && Grouping == LibraryGrouping.Genre;

    public bool HasGenreFilters => IsGenreGrouping && GenreFilters.Count > 1;

    public GenreFilter? SelectedGenreFilter
    {
        get => _selectedGenreFilter;
        set
        {
            if (_buildingGenreFilters || ReferenceEquals(_selectedGenreFilter, value))
            {
                return;
            }

            _selectedGenreFilter = value;
            OnPropertyChanged();
            RebuildGames(restartAtStart: true);
        }
    }

    /// <summary>
    /// Section grouping owned by Big Picture. It starts at "All games" on every
    /// open and never mirrors or changes the library window.
    /// </summary>
    public LibraryGrouping Grouping => _grouping;

    /// <summary>List heading: "Recent games", the active grouping or favorites.</summary>
    public string ListTitle => _favoritesOnly
        ? Loc.T("Loc.BP.GroupFavorites")
        : Grouping switch
        {
            LibraryGrouping.Alphabet => Loc.T("Loc.BP.GroupAlpha"),
            LibraryGrouping.Genre => Loc.T("Loc.BP.GroupGenre"),
            _ => Loc.T("Loc.BP.Recent"),
        };

    /// <summary>Section highlighted in the top block.</summary>
    public BpSection Section => _favoritesOnly
        ? BpSection.Favorites
        : Grouping switch
        {
            LibraryGrouping.Alphabet => BpSection.Alphabet,
            LibraryGrouping.Genre => BpSection.Genre,
            _ => BpSection.All,
        };

    /// <summary>Switches between the flat list, alphabet, genres and favorites.</summary>
    public void SetSection(BpSection section)
    {
        if (Section == section)
        {
            return;
        }

        _favoritesOnly = section == BpSection.Favorites;

        if (!_favoritesOnly)
        {
            _grouping = section switch
            {
                BpSection.Alphabet => LibraryGrouping.Alphabet,
                BpSection.Genre => LibraryGrouping.Genre,
                _ => LibraryGrouping.None,
            };
        }

        OnPropertyChanged(nameof(Grouping));
        OnPropertyChanged(nameof(Section));
        OnPropertyChanged(nameof(ListTitle));

        Reload(restartAtStart: true);

        if (IsGenreGrouping)
        {
            // New games may not have a genre yet: fetch it and refresh the chips.
            _ = _main.EnsureGenresAsync();
        }
    }

    public void MoveGenre(int direction)
    {
        if (GenreFilters.Count == 0)
        {
            return;
        }

        var current = SelectedGenreFilter ?? GenreFilters[0];
        var index = GenreFilters.IndexOf(current);
        if (index < 0)
        {
            index = 0;
        }

        var next = (index + direction + GenreFilters.Count) % GenreFilters.Count;
        SelectedGenreFilter = GenreFilters[next];
    }

    public string StatusText => _selectedGame is null
        ? Loc.T("Loc.BP.Idle")
        : Loc.Format("Loc.BP.Launch", _selectedGame.Title);

    public string EmptyTitle => Loc.T(_favoritesOnly
        ? "Loc.BP.Empty.Favorites.Title"
        : "Loc.BP.Empty.Title");

    public string EmptyText => Loc.T(_favoritesOnly
        ? "Loc.BP.Empty.Favorites.Text"
        : "Loc.BP.Empty.Text");

    /// <summary>
    /// Compact grid instead of the carousel. Down enters it from the carousel;
    /// Up on the first row returns to the carousel.
    /// </summary>
    public bool IsGridMode
    {
        get => _isGridMode;
        set
        {
            if (_isGridMode == value)
            {
                return;
            }

            _isGridMode = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Stars or unstars the selected game; returns its new state.</summary>
    public bool? ToggleFavorite()
    {
        if (_selectedGame is not { } game)
        {
            return null;
        }

        var favorite = !game.IsFavorite;
        _main.ToggleFavorite(game);

        // In the star section a removed game has to leave the list at once.
        if (_favoritesOnly)
        {
            RebuildGames();
        }

        return favorite;
    }

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

    /// <summary>Stops listening to shared state when the Big Picture window closes.</summary>
    public void Detach()
    {
        _clock.Stop();
        Loc.Changed -= OnLanguageChanged;
        _main.Games.CollectionChanged -= OnGamesChanged;
        _main.GenresUpdated -= OnGenresUpdated;
    }

    /// <summary>Genre values arrived from the store: rebuild the chips and order.</summary>
    private void OnGenresUpdated()
    {
        if (IsGenreGrouping)
        {
            Reload();
        }
    }

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

            if (IsGenreGrouping)
            {
                // A scan added games whose genre may still be missing.
                _ = _main.EnsureGenresAsync();
            }
        }), DispatcherPriority.Background);
    }

    private void Reload(bool restartAtStart = false)
    {
        RebuildGenreFilters();
        RebuildGames(restartAtStart);
    }

    private void RebuildGames(bool restartAtStart = false)
    {
        var currentId = _selectedGame?.Id;
        var grouping = _favoritesOnly ? LibraryGrouping.None : Grouping;

        IEnumerable<Game> source = _main.Games;

        if (_favoritesOnly)
        {
            source = source.Where(game => game.IsFavorite);
        }
        else if (IsGenreGrouping && SelectedGenreFilter?.Genre is { } genre)
        {
            source = source.Where(game =>
                string.Equals(GenreOf(game), genre, StringComparison.CurrentCultureIgnoreCase));
        }

        IEnumerable<Game> ordered = grouping == LibraryGrouping.None
            ? source
                .OrderByDescending(game => _store.LastPlayed(game.Id))
                .ThenBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase)
            : source
                .OrderBy(game => GroupRank(GroupOf(game)))
                .ThenBy(GroupOf, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase);

        var orderedGames = ordered.ToList();

        Games.Clear();
        foreach (var game in orderedGames)
        {
            Games.Add(game);
        }

        // Switching a section or genre always lands on the first card of the
        // natural order. Other reloads (a scan, a language change) keep the
        // current card if it is still in the list.
        var preferred = restartAtStart
            ? null
            : Games.FirstOrDefault(game =>
                string.Equals(game.Id, currentId, StringComparison.Ordinal));

        SelectedGame = preferred ?? Games.FirstOrDefault();

        OnPropertyChanged(nameof(HasGames));
        OnPropertyChanged(nameof(GamesCountText));
        OnPropertyChanged(nameof(CounterText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyText));
    }

    /// <summary>Builds the genre chips once per reload, keeping the chosen genre.</summary>
    private void RebuildGenreFilters()
    {
        _buildingGenreFilters = true;
        try
        {
            var previous = _selectedGenreFilter?.Genre;
            GenreFilters.Clear();

            if (!IsGenreGrouping)
            {
                _selectedGenreFilter = null;
                return;
            }

            GenreFilters.Add(new GenreFilter(
                Loc.T("Loc.BP.AllGenres"),
                genre: null,
                count: 0,
                showCount: false));

            var groups = _main.Games
                .Select(GenreOf)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .GroupBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase);

            foreach (var group in groups)
            {
                GenreFilters.Add(new GenreFilter(
                    group.Key,
                    group.Key,
                    group.Count(),
                    showCount: true));
            }

            _selectedGenreFilter = GenreFilters.FirstOrDefault(filter =>
                    string.Equals(filter.Genre, previous, StringComparison.CurrentCultureIgnoreCase))
                ?? GenreFilters[0];
        }
        finally
        {
            _buildingGenreFilters = false;
            OnPropertyChanged(nameof(SelectedGenreFilter));
            OnPropertyChanged(nameof(IsGenreGrouping));
            OnPropertyChanged(nameof(HasGenreFilters));
        }
    }

    /// <summary>Localized genre of a game, or an empty string when it is unknown.</summary>
    private string GenreOf(Game game)
    {
        var stored = _store.GetGenre(game.Id);
        return string.IsNullOrWhiteSpace(stored) ? string.Empty : Loc.GenreName(stored);
    }

    /// <summary>Header for the active section, computed without the library window.</summary>
    private string GroupOf(Game game) => Grouping switch
    {
        LibraryGrouping.Alphabet => LibraryGroupingHelper.AlphabetGroup(game.Title),
        LibraryGrouping.Genre => GenreOf(game) is { Length: > 0 } genre
            ? genre
            : LibraryGroupingHelper.OtherGenre,
        _ => string.Empty,
    };

    /// <summary>Pushes the "Other genre" bucket to the end of the list.</summary>
    private int GroupRank(string? group) => Grouping switch
    {
        LibraryGrouping.Genre when group == LibraryGroupingHelper.OtherGenre => 1,
        _ => 0,
    };

    private void OnLanguageChanged()
    {
        Reload();
        OnPropertyChanged(nameof(ListTitle));
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedSubtitle));
        OnPropertyChanged(nameof(SelectedFolder));
        OnPropertyChanged(nameof(SelectedPlaytimeText));
        OnPropertyChanged(nameof(HasPlaytime));
        OnPropertyChanged(nameof(PlaytimePillText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(GamesCountText));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(CounterText));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>Sections of the Big Picture top block.</summary>
public enum BpSection
{
    All,
    Alphabet,
    Genre,
    Favorites,
}
