using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using BagaGame.Models;
using BagaGame.Services;

namespace BagaGame.ViewModels;

/// <summary>
/// State of the main window: the scanned library, scan status, cover loading
/// and the user's renames / hidden games / extra folders.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly GameLibraryService _libraryService;
    private readonly LibraryStore _store;
    private readonly PlaytimeTracker _playtime;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ListCollectionView _view;

    private bool _isScanning;
    private bool _initialized;
    private bool _downloadCovers;
    private bool _genresLoading;
    private LibraryGrouping _grouping;
    private string _searchQuery = string.Empty;
    private string _statusText = "Подготовка…";
    private string _scanInfo = string.Empty;
    private DateTime? _lastScan;

    public MainViewModel(GameLibraryService libraryService, LibraryStore store)
    {
        _libraryService = libraryService;
        _store = store;
        _downloadCovers = store.DownloadCovers;

        // Watches a launched game and grows its played time while it runs.
        _playtime = new PlaytimeTracker(_store, _libraryService);
        _playtime.PlaytimeChanged += OnPlaytimeChanged;

        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(Games);
        _view.CustomSort = new LibraryComparer(this);
        _view.Filter = FilterGame;

        _grouping = Enum.TryParse<LibraryGrouping>(store.Grouping, out var grouping)
            ? grouping
            : LibraryGrouping.None;

        if (_grouping != LibraryGrouping.None)
        {
            _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Game.Group)));
        }

        Games.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(GamesCount));
            OnPropertyChanged(nameof(LibrarySummary));
            OnPropertyChanged(nameof(VisibleCount));
            OnPropertyChanged(nameof(HasVisibleGames));
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyText));
        };

        Folders.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(FoldersCount));
            OnPropertyChanged(nameof(FoldersSummary));
            OnPropertyChanged(nameof(HasFolders));
        };

        foreach (var folder in store.Folders)
        {
            Folders.Add(folder);
        }
    }

    public ObservableCollection<Game> Games { get; } = new();

    public ObservableCollection<string> Folders { get; } = new();

    /// <summary>Filtered, sorted and grouped view bound to the library grid.</summary>
    public ICollectionView GamesView => _view;

    public int GamesCount => Games.Count;

    public int VisibleCount => _view.Count;

    public bool HasVisibleGames => !_view.IsEmpty;

    public LibraryGrouping Grouping => _grouping;

    public bool IsFiltered => !string.IsNullOrWhiteSpace(_searchQuery);

    public string EmptyTitle => GamesCount == 0 ? "Игры не найдены" : "Ничего не найдено";

    public string EmptyText => GamesCount == 0
        ? "Нажмите «Пересканировать» или добавьте игру кнопкой «Добавить игру»."
        : "Попробуйте другой запрос или очистите поиск.";

    /// <summary>Live search over titles, sources and group names.</summary>
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (string.Equals(_searchQuery, value, StringComparison.Ordinal))
            {
                return;
            }

            _searchQuery = value ?? string.Empty;
            OnPropertyChanged();
            RefreshView();
        }
    }

    public int FoldersCount => Folders.Count;

    public bool HasFolders => Folders.Count > 0;

    public int HiddenCount => _store.HiddenCount;

    public bool HasHidden => HiddenCount > 0;

    public string HiddenSummary => HiddenCount == 0
        ? "Нет скрытых игр"
        : $"Скрыто игр: {HiddenCount}";

    public string FoldersSummary => FoldersCount == 0
        ? "Добавьте папки с играми вне Steam, Epic и GOG"
        : $"Добавлено папок: {FoldersCount}";

    public string LibrarySummary
    {
        get
        {
            if (GamesCount == 0)
            {
                return "Библиотека пуста";
            }

            var count = $"{GamesCount} {Pluralize(GamesCount, "игра", "игры", "игр")}";
            var text = _lastScan is null ? count : $"{count} · обновлено в {_lastScan.Value:HH:mm}";

            return IsFiltered ? $"{text} · показано: {VisibleCount}" : text;
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string ScanInfo
    {
        get => _scanInfo;
        private set
        {
            if (SetField(ref _scanInfo, value))
            {
                OnPropertyChanged(nameof(HasScanInfo));
            }
        }
    }

    public bool HasScanInfo => !string.IsNullOrWhiteSpace(ScanInfo);

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetField(ref _isScanning, value))
            {
                OnPropertyChanged(nameof(CanRescan));
            }
        }
    }

    public bool CanRescan => !IsScanning;

    public bool DownloadCovers
    {
        get => _downloadCovers;
        set
        {
            if (!SetField(ref _downloadCovers, value))
            {
                return;
            }

            _store.DownloadCovers = value;

            if (value)
            {
                _ = LoadCoversAsync(_lifetime.Token);
            }
            else
            {
                // Hide the posters immediately; they are re-read from the cache.
                foreach (var game in Games)
                {
                    game.Cover = null;
                }
            }
        }
    }

    public string CoverCacheDirectory => _libraryService.CoverCacheDirectory;

    public bool IsCustomCoverCache => _store.CoverCacheDirectory is not null;

    /// <summary>
    /// Moves the cover cache to another folder (existing posters are copied, not
    /// re-downloaded) and reloads the cards from the new location.
    /// </summary>
    public async Task ChangeCoverCacheDirectoryAsync(string? directory)
    {
        if (!_libraryService.TrySetCacheDirectory(directory))
        {
            StatusText = "Не удалось использовать эту папку";
            return;
        }

        _store.CoverCacheDirectory = string.Equals(
            _libraryService.CoverCacheDirectory,
            GameLibraryService.DefaultCoverCacheDirectory,
            StringComparison.OrdinalIgnoreCase)
            ? null
            : directory;

        OnPropertyChanged(nameof(CoverCacheDirectory));
        OnPropertyChanged(nameof(IsCustomCoverCache));

        foreach (var game in Games)
        {
            game.Cover = null;
        }

        StatusText = "Кэш обложек обновлён";
        await LoadCoversAsync(_lifetime.Token);
    }

    /// <summary>Scans the machine automatically on the first window load.</summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await RescanAsync();
    }

    public async Task RescanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        IsScanning = true;
        StatusText = "Сканирование…";

        try
        {
            var outcome = await _libraryService.ScanAsync(
                Folders.ToList(),
                _lifetime.Token);

            var games = _store.Apply(MergeManualGames(outcome.Games));
            ReplaceGames(games);

            _lastScan = DateTime.Now;
            OnPropertyChanged(nameof(LibrarySummary));

            StatusText = Games.Count == 0
                ? "Игры не найдены"
                : "Сканирование завершено";

            ScanInfo = BuildScanInfo(outcome, games.Count(game => game.Source == GameLibraryService.ManualSource));
            NotifyHiddenChanged();

            await LoadCoversAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Сканирование отменено";
        }
        catch
        {
            StatusText = "Не удалось просканировать";
        }
        finally
        {
            IsScanning = false;
        }
    }

    public bool RenameGame(Game game, string title)
    {
        var clean = title.Trim();
        if (clean.Length == 0 || string.Equals(clean, game.Title, StringComparison.Ordinal))
        {
            return false;
        }

        _store.Rename(game.Id, clean);
        game.Title = clean;
        ApplyGroups();
        RefreshView();
        return true;
    }

    /// <summary>
    /// Hides the game: it leaves the library but keeps its name and genre, and
    /// "Восстановить скрытые" in the settings brings it back.
    /// </summary>
    public void RemoveGame(Game game)
    {
        _store.Hide(game.Id);
        Games.Remove(game);
        NotifyHiddenChanged();
    }

    /// <summary>
    /// Deletes the game's record (rename, genre, played time, cached AppID and
    /// poster) and takes the card out of the library, but does not mark it as
    /// hidden. A later rescan finds a scanned game again as a new entry; a
    /// manually added game stays gone until it is added again.
    /// </summary>
    public void DeleteGame(Game game)
    {
        var title = game.Title;

        _store.RemoveManualGame(game.Id);
        _store.ForgetGame(game.Id);

        try
        {
            _libraryService.ForgetGameArt(game, Games.Where(item => !ReferenceEquals(item, game)).ToList());
        }
        catch
        {
            // Cached art is decorative; the removal itself already happened.
        }

        Games.Remove(game);
        StatusText = $"Удалено: {title}";
    }

    /// <summary>Starts the game with its executable or through the Steam client.</summary>
    public bool LaunchGame(Game game)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(game.ExecutablePath) &&
                File.Exists(game.ExecutablePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = game.ExecutablePath,
                    WorkingDirectory = Path.GetDirectoryName(game.ExecutablePath) ?? string.Empty,
                    UseShellExecute = true,
                });

                StatusText = $"Запуск: {game.Title}";
                _playtime.Track(game);
                return true;
            }

            if (game.AppId > 0)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"steam://rungameid/{game.AppId}",
                    UseShellExecute = true,
                });

                StatusText = $"Запуск: {game.Title}";
                _playtime.Track(game);
                return true;
            }

            StatusText = "Не найден файл запуска";
            return false;
        }
        catch
        {
            StatusText = $"Не удалось запустить: {game.Title}";
            return false;
        }
    }

    public void OpenGameFolder(Game game)
    {
        if (string.IsNullOrWhiteSpace(game.InstallPath) ||
            !Directory.Exists(game.InstallPath))
        {
            StatusText = "Папка игры не найдена";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = game.InstallPath,
                UseShellExecute = true,
            });
        }
        catch
        {
            StatusText = "Не удалось открыть папку";
        }
    }

    public async Task RestoreHiddenAsync()
    {
        _store.RestoreHidden();
        NotifyHiddenChanged();
        await RescanAsync();
    }

    /// <summary>
    /// Adds a single game (a folder or an .exe) to the library. The rest of the
    /// library is not rescanned; covers and genres are fetched as usual.
    /// </summary>
    public async Task<bool> AddSingleGameAsync(string path)
    {
        var game = GameLibraryService.CreateManualGame(path);

        if (game is null)
        {
            StatusText = "В выбранной папке не найден .exe игры";
            return false;
        }

        var existing = Games.FirstOrDefault(item => SameGamePath(item, game));
        if (existing is not null)
        {
            StatusText = $"Уже в библиотеке: {existing.Title}";
            return false;
        }

        _store.AddManualGame(new ManualGameEntry
        {
            Id = game.Id,
            Title = game.Title,
            InstallPath = game.InstallPath ?? string.Empty,
            ExecutablePath = game.ExecutablePath ?? string.Empty,
        });

        await RescanAsync();
        StatusText = $"Добавлено: {game.Title}";
        return true;
    }

    /// <summary>Switches the group headers and remembers the choice.</summary>
    public async Task SetGroupingAsync(LibraryGrouping grouping)
    {
        if (_grouping == grouping)
        {
            return;
        }

        _grouping = grouping;
        _store.Grouping = grouping.ToString();
        OnPropertyChanged(nameof(Grouping));

        _view.GroupDescriptions.Clear();
        if (grouping != LibraryGrouping.None)
        {
            _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Game.Group)));
        }

        ApplyGroups();
        RefreshView();

        if (grouping == LibraryGrouping.Genre)
        {
            await LoadGenresAsync();
        }
    }

    public async Task AddFolderAsync(string path)
    {
        _store.AddFolder(path);

        Folders.Clear();
        foreach (var folder in _store.Folders)
        {
            Folders.Add(folder);
        }

        await RescanAsync();
    }

    public async Task RemoveFolderAsync(string path)
    {
        _store.RemoveFolder(path);

        var existing = Folders.FirstOrDefault(
            item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            Folders.Remove(existing);
        }

        await RescanAsync();
    }

    private static string BuildScanInfo(ScanOutcome outcome, int manualCount)
    {
        if (outcome.Games.Count == 0 && manualCount == 0)
        {
            return "Ничего не найдено. Установите Steam, Epic Games или GOG Galaxy либо добавьте папку с играми.";
        }

        var parts = new List<string>
        {
            $"{GameLibraryService.SteamSource}: {CountOf(outcome, GameLibraryService.SteamSource)}",
            $"{GameLibraryService.EpicSource}: {CountOf(outcome, GameLibraryService.EpicSource)}",
            $"{GameLibraryService.GogSource}: {CountOf(outcome, GameLibraryService.GogSource)}",
            $"Папки: {CountOf(outcome, GameLibraryService.FolderSource)}",
        };

        if (manualCount > 0)
        {
            parts.Add($"{GameLibraryService.ManualSource}: {manualCount}");
        }

        return string.Join(" · ", parts);
    }

    private static int CountOf(ScanOutcome outcome, string source)
        => outcome.SourceCounts.TryGetValue(source, out var count) ? count : 0;

    private async Task LoadCoversAsync(CancellationToken cancellationToken)
    {
        if (!DownloadCovers)
        {
            return;
        }

        // Update each card as soon as its cover arrives; the library stays usable.
        var tasks = Games
            .Where(game => game.Cover is null)
            .Select(game => LoadCoverAsync(game, cancellationToken))
            .ToArray();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // Window closing.
        }
        catch
        {
            // Covers are decorative; never fail the library because of them.
        }
    }

    private async Task LoadCoverAsync(Game game, CancellationToken cancellationToken)
    {
        if (game.Cover is not null)
        {
            return;
        }

        try
        {
            // Decode on the thread pool: cached covers are read synchronously and
            // must not stall the UI thread while the window is showing.
            var cover = await Task.Run(
                () => _libraryService.GetCoverAsync(game, cancellationToken),
                cancellationToken);

            if (cover is not null)
            {
                game.Cover = cover;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Placeholder stays visible.
        }
    }

    /// <summary>
    /// Manual entries are added on top of the scanned games. Missing paths are
    /// dropped, and an entry that is already covered by a scanned folder is
    /// merged instead of shown twice.
    /// </summary>
    private List<Game> MergeManualGames(IEnumerable<Game> scannedGames)
    {
        var result = new List<Game>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var executablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in scannedGames)
        {
            if (!ids.Add(game.Id))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(game.ExecutablePath))
            {
                executablePaths.Add(game.ExecutablePath);
            }

            result.Add(game);
        }

        foreach (var entry in _store.ManualGames.ToList())
        {
            if (string.IsNullOrWhiteSpace(entry.InstallPath) ||
                string.IsNullOrWhiteSpace(entry.ExecutablePath) ||
                !Directory.Exists(entry.InstallPath) ||
                !File.Exists(entry.ExecutablePath))
            {
                _store.RemoveManualGame(entry.Id);
                continue;
            }

            // Older builds used the executable name and the deepest technical
            // folder. Rebuild the readable title/root from the executable and
            // persist the repaired values, keeping the old id so renames and
            // hidden state still match.
            var normalized = GameLibraryService.CreateManualGame(entry.ExecutablePath);
            if (normalized is not null &&
                (!string.Equals(entry.Title, normalized.Title, StringComparison.Ordinal) ||
                 !string.Equals(
                     entry.InstallPath,
                     normalized.InstallPath,
                     StringComparison.OrdinalIgnoreCase)))
            {
                entry.Title = normalized.Title;
                entry.InstallPath = normalized.InstallPath ?? entry.InstallPath;
                _store.UpdateManualGame(entry);
            }

            if (!executablePaths.Add(entry.ExecutablePath))
            {
                // The folder is now scanned automatically or the same .exe was
                // added twice; keep one card.
                _store.RemoveManualGame(entry.Id);
                continue;
            }

            if (!ids.Add(entry.Id))
            {
                continue;
            }

            result.Add(new Game
            {
                Id = entry.Id,
                Source = GameLibraryService.ManualSource,
                Title = entry.Title,
                InstallPath = entry.InstallPath,
                ExecutablePath = entry.ExecutablePath,
            });
        }

        return result;
    }

    private static bool SameGamePath(Game left, Game right)
    {
        if (!string.IsNullOrWhiteSpace(left.ExecutablePath) &&
            string.Equals(left.ExecutablePath, right.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(left.InstallPath) &&
               string.Equals(
                   NormalizePath(left.InstallPath),
                   NormalizePath(right.InstallPath),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Trim().TrimEnd('\\', '/');

    /// <summary>Assigns a header to every game according to the current mode.</summary>
    private void ApplyGroups()
    {
        var series = _grouping == LibraryGrouping.Series
            ? LibraryGroupingHelper.BuildSeriesMap(Games)
            : null;

        foreach (var game in Games)
        {
            game.Group = _grouping switch
            {
                LibraryGrouping.Alphabet => LibraryGroupingHelper.AlphabetGroup(game.Title),
                LibraryGrouping.Genre => _store.GetGenre(game.Id) ?? LibraryGroupingHelper.OtherGenre,
                LibraryGrouping.Series => series!.TryGetValue(game.Id, out var name)
                    ? name
                    : LibraryGroupingHelper.NoSeries,
                _ => string.Empty,
            };
        }
    }

    private void RefreshView()
    {
        _view.Refresh();

        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(HasVisibleGames));
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(LibrarySummary));
    }

    private bool FilterGame(object item)
    {
        if (item is not Game game || !IsFiltered)
        {
            return item is Game;
        }

        var query = _searchQuery.Trim();

        return Matches(game.Title) ||
               Matches(game.Source) ||
               Matches(game.Group) ||
               Matches(game.InstallPath);

        bool Matches(string? value)
            => !string.IsNullOrWhiteSpace(value) &&
               value.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>
    /// Steam genres are fetched once per game and remembered in library.json.
    /// Group headers appear immediately and fill in as answers arrive.
    /// </summary>
    private async Task LoadGenresAsync()
    {
        if (_genresLoading)
        {
            return;
        }

        var missing = Games
            .Where(game => string.IsNullOrWhiteSpace(_store.GetGenre(game.Id)))
            .ToList();

        if (missing.Count == 0)
        {
            ApplyGroups();
            RefreshView();
            return;
        }

        _genresLoading = true;
        var total = missing.Count;
        var finished = 0;
        StatusText = $"Определение жанров… 0/{total}";

        try
        {
            var tasks = missing.Select(async game =>
            {
                var genre = await _libraryService.GetGenreAsync(game, _lifetime.Token);
                if (!string.IsNullOrWhiteSpace(genre))
                {
                    _store.SetGenre(game.Id, genre);
                }

                finished++;
                StatusText = $"Определение жанров… {finished}/{total}";
            });

            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // Window closing.
        }
        catch
        {
            // A game without a genre simply stays in "Другие".
        }
        finally
        {
            _genresLoading = false;
        }

        if (_grouping == LibraryGrouping.Genre)
        {
            ApplyGroups();
            RefreshView();
            StatusText = "Жанры определены";
        }
    }

    private int GroupRank(string? group) => _grouping switch
    {
        LibraryGrouping.Series when group == LibraryGroupingHelper.NoSeries => 1,
        LibraryGrouping.Genre when group == LibraryGroupingHelper.OtherGenre => 1,
        _ => 0,
    };

    /// <summary>Orders games by group, then by title (culture-aware).</summary>
    private sealed class LibraryComparer : IComparer
    {
        private readonly MainViewModel _owner;

        public LibraryComparer(MainViewModel owner)
        {
            _owner = owner;
        }

        public int Compare(object? x, object? y)
        {
            var left = x as Game;
            var right = y as Game;

            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            var rank = _owner.GroupRank(left.Group).CompareTo(_owner.GroupRank(right.Group));
            if (rank != 0)
            {
                return rank;
            }

            var group = string.Compare(
                left.Group,
                right.Group,
                StringComparison.CurrentCultureIgnoreCase);

            return group != 0
                ? group
                : string.Compare(left.Title, right.Title, StringComparison.CurrentCultureIgnoreCase);
        }
    }

    private void ReplaceGames(IEnumerable<Game> games)
    {
        Games.Clear();
        foreach (var game in games)
        {
            Games.Add(game);
        }

        // Played time is remembered per game id, so a rescan restores it.
        foreach (var game in Games)
        {
            game.PlaytimeSeconds = _store.GetPlaytime(game.Id);
        }

        ApplyGroups();
        RefreshView();

        OnPropertyChanged(nameof(GamesCount));
        OnPropertyChanged(nameof(LibrarySummary));

        if (_grouping == LibraryGrouping.Genre &&
            Games.Any(game => string.IsNullOrWhiteSpace(_store.GetGenre(game.Id))))
        {
            _ = LoadGenresAsync();
        }
    }

    private void NotifyHiddenChanged()
    {
        OnPropertyChanged(nameof(HiddenCount));
        OnPropertyChanged(nameof(HasHidden));
        OnPropertyChanged(nameof(HiddenSummary));
    }

    /// <summary>Keeps the card and library.json in sync with the tracker.</summary>
    private void OnPlaytimeChanged(string gameId, long seconds)
    {
        _store.SetPlaytime(gameId, seconds);

        var game = Games.FirstOrDefault(
            item => string.Equals(item.Id, gameId, StringComparison.OrdinalIgnoreCase));

        if (game is not null)
        {
            game.PlaytimeSeconds = seconds;
        }
    }

    private static string Pluralize(int count, string one, string few, string many)
    {
        var lastTwo = count % 100;
        if (lastTwo is >= 11 and <= 14)
        {
            return many;
        }

        return (count % 10) switch
        {
            1 => one,
            >= 2 and <= 4 => few,
            _ => many,
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _playtime.PlaytimeChanged -= OnPlaytimeChanged;
        _playtime.Dispose();
        _libraryService.Dispose();
    }
}
