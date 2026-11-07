using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Media.Imaging;
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
    private readonly CpuTopology _cpuTopology;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ListCollectionView _view;

    private bool _isScanning;
    private bool _initialized;
    private bool _downloadCovers;
    private bool _genresLoading;
    private LibraryGrouping _grouping;
    private string _searchQuery = string.Empty;
    private bool _favoritesOnly;
    private string _statusText = Loc.T("Loc.Status.Ready");
    private string _scanInfo = string.Empty;
    private DateTime? _lastScan;

    // The status keeps its resource key so a language switch re-renders it.
    private string? _statusKey;
    private object?[]? _statusArgs;

    // Update check: the release number is cached in library.json, so the card
    // appears immediately on the next launch without another request.
    private const long UpdateCacheSeconds = 12 * 60 * 60;

    private readonly UpdateService _updates = new();
    private bool _updateAvailable;
    private bool _checkingUpdate;
    private string _updateVersion = string.Empty;
    private string _updateUrl = string.Empty;
    private string _updateStatusText = string.Empty;
    private string? _updateStatusKey;
    private object?[]? _updateStatusArgs;

    public MainViewModel(GameLibraryService libraryService, LibraryStore store)
    {
        _libraryService = libraryService;
        _store = store;

        // Optimisation is an explicit per-session action: every launcher start
        // begins with the master switch off.
        _store.OptimizeGames = false;

        _cpuTopology = CpuTopologyService.Detect();
        _downloadCovers = store.DownloadCovers;

        // Watches a launched game and grows its played time while it runs.
        _playtime = new PlaytimeTracker(_store, _libraryService);
        _playtime.PlaytimeChanged += OnPlaytimeChanged;
        _playtime.GameStopped += OnGameStopped;

        // Every visible label is re-rendered when the language is switched.
        Loc.Changed += OnLanguageChanged;

        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(Games);
        _view.CustomSort = new LibraryComparer(this);
        _view.Filter = FilterGame;

        // The library always opens on "All games"; the grouping is a session
        // choice and is not restored from library.json.
        _grouping = LibraryGrouping.None;

        Games.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(GamesCount));
            OnPropertyChanged(nameof(GamesCountText));
            OnPropertyChanged(nameof(LibrarySummary));
            OnPropertyChanged(nameof(VisibleCount));
            OnPropertyChanged(nameof(VisibleCountText));
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

        RefreshHiddenGames();

        // Show the cached release number before the first request finishes.
        RestoreKnownUpdate();
    }

    /// <summary>Raised when a launched game closes; the window leaves the tray.</summary>
    public event Action<string>? GameStopped;

    /// <summary>New genre values were fetched and stored for one or more games.</summary>
    public event Action? GenresUpdated;

    public ObservableCollection<Game> Games { get; } = new();

    public ObservableCollection<string> Folders { get; } = new();

    /// <summary>Hidden games listed in Settings, each with its own restore button.</summary>
    public ObservableCollection<HiddenGameItem> HiddenGames { get; } = new();

    /// <summary>Online covers shown in the cover picker.</summary>
    public ObservableCollection<CoverCandidate> CoverResults { get; } = new();

    /// <summary>Wide Big Picture banners shown in the cover picker.</summary>
    public ObservableCollection<CoverCandidate> HeroResults { get; } = new();

    /// <summary>Games that can show achievements, bound to the Achievements tab.</summary>
    public ObservableCollection<AchievementGameItem> AchievementGames { get; } = new();

    /// <summary>Achievements of the selected supported game, shown as tiles.</summary>
    public ObservableCollection<SteamAchievement> AchievementTiles { get; } = new();

    private string _coverStatusText = string.Empty;

    public string CoverStatusText
    {
        get => _coverStatusText;
        set => SetField(ref _coverStatusText, value);
    }

    /// <summary>Filtered, sorted and grouped view bound to the library grid.</summary>
    public ICollectionView GamesView => _view;

    /// <summary>Artwork, metadata and store lookups used by Big Picture.</summary>
    public GameLibraryService LibraryService => _libraryService;

    public int GamesCount => Games.Count;

    public int VisibleCount => _view.Count;

    public string VersionSummary =>
        $"{Loc.Format("Loc.App.Version", AppInfo.VersionNumber)} · {AppInfo.Runtime}";

    public string GamesCountText => Loc.Format("Loc.Library.FoundGames", GamesCount);

    public string VisibleCountText => Loc.Format("Loc.Library.Shown", VisibleCount);

    public bool HasVisibleGames => !_view.IsEmpty;

    // ------------------------------------------------------------------
    // Achievements tab
    // ------------------------------------------------------------------

    public int AchievementGamesCount => AchievementGames.Count;

    public bool HasAchievementGames => AchievementGames.Count > 0;

    public string AchievementGamesCountText =>
        Loc.Format("Loc.Achievements.SupportedCount", AchievementGamesCount);

    private AchievementGameItem? _selectedAchievementGame;

    /// <summary>The game whose achievements are shown on the right.</summary>
    public AchievementGameItem? SelectedAchievementGame
    {
        get => _selectedAchievementGame;
        set
        {
            if (ReferenceEquals(_selectedAchievementGame, value))
            {
                return;
            }

            _selectedAchievementGame = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedAchievementGame));
            _ = LoadSelectedAchievementsAsync();
        }
    }

    public bool HasSelectedAchievementGame => _selectedAchievementGame is not null;

    private string _selectedAchievementProgress = string.Empty;

    public string SelectedAchievementProgress
    {
        get => _selectedAchievementProgress;
        private set => SetField(ref _selectedAchievementProgress, value);
    }

    private string _selectedAchievementStatus = string.Empty;

    public string SelectedAchievementStatus
    {
        get => _selectedAchievementStatus;
        private set => SetField(ref _selectedAchievementStatus, value);
    }

    private bool _loadingAchievements;

    public bool IsLoadingAchievements
    {
        get => _loadingAchievements;
        private set => SetField(ref _loadingAchievements, value);
    }

    public bool HasAchievementTiles => AchievementTiles.Count > 0;

    /// <summary>
    /// Rebuilds the supported-games list. Safe to call after every scan and
    /// when the user asks for a refresh; the selection is preserved by id.
    /// </summary>
    public void RefreshAchievementGames()
    {
        var selectedId = _selectedAchievementGame?.Game.Id;

        AchievementGames.Clear();

        var items = new List<AchievementGameItem>();

        foreach (var game in Games)
        {
            try
            {
                var support = AchievementSupportService.Describe(
                    game,
                    GetSteamAppId(game),
                    _store.IsAchievementVerified(game.Id));
                if (support is null)
                {
                    continue;
                }

                items.Add(new AchievementGameItem(
                    game,
                    support.Platform,
                    support.HasCatalog,
                    support.Verified,
                    support.LocalCount,
                    support.StatusText));
            }
            catch
            {
                // One unreadable game only skips the row.
            }
        }

        foreach (var item in items
                     .OrderBy(item => item.Game.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            AchievementGames.Add(item);
        }

        OnPropertyChanged(nameof(AchievementGamesCount));
        OnPropertyChanged(nameof(HasAchievementGames));
        OnPropertyChanged(nameof(AchievementGamesCountText));

        var keep = selectedId is null
            ? null
            : AchievementGames.FirstOrDefault(item =>
                string.Equals(item.Game.Id, selectedId, StringComparison.OrdinalIgnoreCase));

        SelectedAchievementGame = keep ?? AchievementGames.FirstOrDefault();
    }

    /// <summary>
    /// The notifier showed a toast, so the game really writes achievements.
    /// Persist that and flip the row badge without reloading the catalogue.
    /// </summary>
    public void MarkAchievementVerified(Game game)
    {
        if (game is null || string.IsNullOrWhiteSpace(game.Id))
        {
            return;
        }

        _store.MarkAchievementVerified(game.Id);

        for (var index = 0; index < AchievementGames.Count; index++)
        {
            var item = AchievementGames[index];
            if (!string.Equals(item.Game.Id, game.Id, StringComparison.OrdinalIgnoreCase) ||
                item.Verified)
            {
                continue;
            }

            var updated = item with
            {
                Verified = true,
                StatusText = Loc.T("Loc.Achievements.Verified"),
            };

            AchievementGames[index] = updated;

            if (ReferenceEquals(_selectedAchievementGame, item))
            {
                _selectedAchievementGame = updated;
                OnPropertyChanged(nameof(SelectedAchievementGame));
            }

            break;
        }
    }

    private async Task LoadSelectedAchievementsAsync()
    {
        var item = _selectedAchievementGame;
        if (item is null)
        {
            AchievementTiles.Clear();
            SelectedAchievementProgress = string.Empty;
            SelectedAchievementStatus = string.Empty;
            OnPropertyChanged(nameof(HasAchievementTiles));
            return;
        }

        var appId = GetSteamAppId(item.Game);
        if (appId <= 0)
        {
            AchievementTiles.Clear();
            SelectedAchievementProgress = string.Empty;
            SelectedAchievementStatus = Loc.T("Loc.Achievements.NoCatalog");
            OnPropertyChanged(nameof(HasAchievementTiles));
            return;
        }

        IsLoadingAchievements = true;

        try
        {
            // Rarity comes from a different Steam endpoint, so it runs next to
            // the profile and catalogue requests instead of after them.
            var percentagesTask = SteamGlobalStatsService.Shared
                .GetAsync(appId, _lifetime.Token);

            var result = await SteamAchievementsService.Shared
                .GetAsync(appId, _lifetime.Token)
                .ConfigureAwait(true);

            // Another game was selected while the catalogue was loading.
            if (!ReferenceEquals(_selectedAchievementGame, item))
            {
                return;
            }

            var catalog = await SteamAchievementCatalogService.Shared
                .GetAsync(appId, SteamAchievementCatalogService.Language, _lifetime.Token)
                .ConfigureAwait(true);

            if (!ReferenceEquals(_selectedAchievementGame, item))
            {
                return;
            }

            var local = new LocalAchievementsService().Get(item.Game, appId);
            var hasCatalog = catalog.Count > 0;

            var merged = AchievementMergeService
                .BuildAchievements(result, local, catalog)
                .OrderByDescending(achievement => achievement.Unlocked)
                .ToList();

            AchievementTimeService.Apply(item.Game, merged, _store);

            AchievementTiles.Clear();
            foreach (var achievement in merged)
            {
                AchievementTiles.Add(achievement);
            }

            var unlocked = merged.Count(achievement => achievement.Unlocked);
            SelectedAchievementProgress = result.Status == SteamAchievementsStatus.Success || hasCatalog
                ? Loc.Format("Loc.Achievements.Progress", unlocked, merged.Count)
                : string.Empty;

            SelectedAchievementStatus = merged.Count > 0
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

            // The list is already visible; rarity fills in as soon as it arrives.
            _ = AssignPercentagesAsync(item, percentagesTask);
        }
        catch (OperationCanceledException)
        {
            // The window closed or the selection changed.
        }
        catch
        {
            SelectedAchievementStatus = Loc.T("Loc.Achievements.Error");
        }
        finally
        {
            IsLoadingAchievements = false;
            OnPropertyChanged(nameof(HasAchievementTiles));
        }
    }

    private async Task AssignPercentagesAsync(
        AchievementGameItem item,
        Task<IReadOnlyDictionary<string, double>> percentagesTask)
    {
        try
        {
            var percentages = await percentagesTask.ConfigureAwait(true);
            if (!ReferenceEquals(_selectedAchievementGame, item))
            {
                return;
            }

            SteamGlobalStatsService.Assign(AchievementTiles, percentages);
        }
        catch (OperationCanceledException)
        {
            // The window closed or the selection changed.
        }
        catch
        {
            // Rarity is optional; the tiles keep the "no data" tooltip.
        }
    }

    public LibraryGrouping Grouping => _grouping;

    public bool IsFiltered => !string.IsNullOrWhiteSpace(_searchQuery);

    /// <summary>True when the cards are narrowed by search or by favourites.</summary>
    public bool IsNarrowed => IsFiltered || FavoritesOnly;

    /// <summary>Star filter: show only games the user marked as favourites.</summary>
    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set
        {
            if (!SetField(ref _favoritesOnly, value))
            {
                return;
            }

            RefreshView();
        }
    }

    public string EmptyTitle => Loc.T(
        FavoritesOnly && FavoriteCount == 0 ? "Loc.Empty.NoFavorites"
        : GamesCount == 0 ? "Loc.Empty.NoGames" : "Loc.Empty.NoMatches");

    public string EmptyText => Loc.T(
        FavoritesOnly && FavoriteCount == 0 ? "Loc.Empty.NoFavoritesHint"
        : GamesCount == 0 ? "Loc.Empty.NoGamesHint" : "Loc.Empty.NoMatchesHint");

    private int FavoriteCount => Games.Count(game => game.IsFavorite);

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
        ? Loc.T("Loc.Settings.HiddenNone")
        : Loc.Format("Loc.Settings.HiddenCount", HiddenCount);

    public string FoldersSummary => FoldersCount == 0
        ? Loc.T("Loc.Folders.None")
        : Loc.Format("Loc.Folders.Count", FoldersCount);

    public string LibrarySummary
    {
        get
        {
            if (GamesCount == 0)
            {
                return Loc.T("Loc.Library.Empty");
            }

            var count = Loc.Format(
                "Loc.Library.SummaryCount",
                GamesCount,
                Loc.Plural(
                    GamesCount,
                    "Loc.Library.CountOne",
                    "Loc.Library.CountFew",
                    "Loc.Library.CountMany"));

            var text = _lastScan is null
                ? count
                : Loc.Format("Loc.Library.UpdatedAt", count, _lastScan.Value.ToString("HH:mm"));

            return IsFiltered
                ? Loc.Format("Loc.Library.Filtered", text, VisibleCount)
                : text;
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

    /// <summary>Open Big Picture when a controller connects and the window is active.</summary>
    public bool BigPictureOnGamepad
    {
        get => _store.BigPictureOnGamepad;
        set
        {
            if (_store.BigPictureOnGamepad == value)
            {
                return;
            }

            _store.BigPictureOnGamepad = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Show the achievement status toast when a game is launched.</summary>
    public bool AchievementScanNotice
    {
        get => _store.AchievementScanNotice;
        set
        {
            if (_store.AchievementScanNotice == value)
            {
                return;
            }

            _store.AchievementScanNotice = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Detected CPU topology used by the performance settings.</summary>
    public CpuTopology CpuTopology => _cpuTopology;

    public string CpuSummary => BuildCpuSummary();

    public bool CanUsePerformanceCores => _cpuTopology.CanForcePerformanceCores;

    public bool CanUseEfficiencyCores => _cpuTopology.CanForceEfficiencyCores;

    public bool CanSelectL3Domain => _cpuTopology.CanSelectL3Domain;

    public string CpuModeHint =>
        !CanUsePerformanceCores &&
        !CanUseEfficiencyCores &&
        !CanSelectL3Domain
            ? Loc.T("Loc.Settings.CpuModeAutoOnly")
            : Loc.T("Loc.Settings.CpuModeSummary");

    public bool OptimizeGames
    {
        get => _store.OptimizeGames;
        set
        {
            if (_store.OptimizeGames == value)
            {
                return;
            }

            _store.OptimizeGames = value;
            OnPropertyChanged();

            // Turning optimisation on starts from the safe universal profile.
            if (value)
            {
                ResetPerformanceDefaults();
            }
        }
    }

    /// <summary>
    /// Auto CPU mode, Auto priority and both optional switches off. Applied
    /// when the master optimisation switch is turned on.
    /// </summary>
    public void ResetPerformanceDefaults()
    {
        CpuMode = CpuOptimizationMode.Auto;
        GamePriority = GameProcessPriority.Normal;
        DisableEcoQos = false;
        LauncherBackground = false;
        TimerResolution = GameTimerResolution.Off;
    }

    /// <summary>CPU mode with unsupported values safely reduced to Auto.</summary>
    public CpuOptimizationMode CpuMode
    {
        get
        {
            var mode = _store.CpuMode;
            if (mode == CpuOptimizationMode.PerformanceCores &&
                !CanUsePerformanceCores)
            {
                return CpuOptimizationMode.Auto;
            }

            if (mode == CpuOptimizationMode.EfficiencyCores &&
                !CanUseEfficiencyCores)
            {
                return CpuOptimizationMode.Auto;
            }

            if (mode == CpuOptimizationMode.PreferredL3Domain &&
                !CanSelectL3Domain)
            {
                return CpuOptimizationMode.Auto;
            }

            return mode;
        }
        set
        {
            if (value == CpuOptimizationMode.PerformanceCores &&
                !CanUsePerformanceCores)
            {
                value = CpuOptimizationMode.Auto;
            }

            if (value == CpuOptimizationMode.EfficiencyCores &&
                !CanUseEfficiencyCores)
            {
                value = CpuOptimizationMode.Auto;
            }

            if (value == CpuOptimizationMode.PreferredL3Domain &&
                !CanSelectL3Domain)
            {
                value = CpuOptimizationMode.Auto;
            }

            if (_store.CpuMode == value)
            {
                return;
            }

            _store.CpuMode = value;
            OnPropertyChanged();
        }
    }

    public GameProcessPriority GamePriority
    {
        get => _store.GamePriority;
        set
        {
            if (_store.GamePriority == value)
            {
                return;
            }

            _store.GamePriority = value;
            OnPropertyChanged();
        }
    }

    public bool DisableEcoQos
    {
        get => _store.DisableEcoQos;
        set
        {
            if (_store.DisableEcoQos == value)
            {
                return;
            }

            _store.DisableEcoQos = value;
            OnPropertyChanged();
        }
    }

    public bool LauncherBackground
    {
        get => _store.LauncherBackground;
        set
        {
            if (_store.LauncherBackground == value)
            {
                return;
            }

            _store.LauncherBackground = value;
            OnPropertyChanged();
        }
    }

    public GameTimerResolution TimerResolution
    {
        get => _store.TimerResolution;
        set
        {
            if (_store.TimerResolution == value)
            {
                return;
            }

            _store.TimerResolution = value;
            OnPropertyChanged();
        }
    }

    private GameBoostSettings CreateBoostSettings()
        => new(
            _store.OptimizeGames,
            CpuMode,
            _store.GamePriority,
            _store.DisableEcoQos,
            _store.LauncherBackground,
            _store.TimerResolution,
            false);

    private string BuildCpuSummary()
    {
        var model = string.IsNullOrWhiteSpace(_cpuTopology.Model)
            ? "CPU"
            : _cpuTopology.Model;
        var logical = _cpuTopology.LogicalProcessors;

        if (_cpuTopology.IsHybrid)
        {
            var performance = _cpuTopology.Cores
                .Where(core => core.IsPerformance)
                .Select(core => (core.Group, core.CoreIndex))
                .Distinct()
                .Count();
            var efficiency = _cpuTopology.Cores
                .Where(core => !core.IsPerformance)
                .Select(core => (core.Group, core.CoreIndex))
                .Distinct()
                .Count();

            return Loc.Format(
                "Loc.Performance.CpuHybrid",
                model,
                performance,
                efficiency,
                logical);
        }

        if (_cpuTopology.CanSelectL3Domain)
        {
            return Loc.Format(
                "Loc.Performance.CpuCcd",
                model,
                _cpuTopology.PhysicalCores,
                _cpuTopology.L3Domains.Count,
                logical);
        }

        return Loc.Format(
            "Loc.Performance.CpuBasic",
            model,
            _cpuTopology.PhysicalCores,
            logical);
    }

    /// <summary>True when GitHub reports a stable release newer than this build.</summary>
    public bool HasUpdate
    {
        get => _updateAvailable;
        private set
        {
            if (SetField(ref _updateAvailable, value))
            {
                OnPropertyChanged(nameof(UpdateVersionText));
                OnPropertyChanged(nameof(UpdateTipText));
            }
        }
    }

    /// <summary>"Version X is available", re-rendered on a language switch.</summary>
    public string UpdateVersionText => HasUpdate
        ? Loc.Format("Loc.Update.Available", _updateVersion)
        : string.Empty;

    public string UpdateTipText => HasUpdate
        ? Loc.Format("Loc.Update.Tip", _updateVersion)
        : string.Empty;

    /// <summary>A newer release is known even though the sidebar card is hidden.</summary>
    public bool HasHiddenUpdate =>
        !HasUpdate && UpdateService.IsNewer(_store.LatestKnownVersion);

    /// <summary>Recovery action in Settings after the user hides the release card.</summary>
    public string UpdateDownloadText => HasHiddenUpdate
        ? Loc.Format("Loc.Update.Download", _store.LatestKnownVersion)
        : string.Empty;

    /// <summary>Status shown in Settings next to the update toggle.</summary>
    public string UpdateStatusText
    {
        get => _updateStatusText;
        private set => SetField(ref _updateStatusText, value);
    }

    public bool IsCheckingUpdate
    {
        get => _checkingUpdate;
        private set
        {
            if (SetField(ref _checkingUpdate, value))
            {
                OnPropertyChanged(nameof(CanCheckForUpdates));
            }
        }
    }

    public bool CanCheckForUpdates => _store.CheckForUpdates && !IsCheckingUpdate;

    /// <summary>Automatic check for a newer release; on by default.</summary>
    public bool CheckForUpdates
    {
        get => _store.CheckForUpdates;
        set
        {
            if (_store.CheckForUpdates == value)
            {
                return;
            }

            _store.CheckForUpdates = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanCheckForUpdates));

            if (!value)
            {
                ClearUpdateStatus();
            }
            else if (!HasUpdate)
            {
                _ = CheckForUpdatesAsync(force: true);
            }
        }
    }

    /// <summary>
    /// Asks GitHub for the newest release. A success is cached for twelve
    /// hours; a failure is not, so the next launch simply tries again.
    /// </summary>
    public async Task CheckForUpdatesAsync(bool force = false)
    {
        if (!_store.CheckForUpdates || IsCheckingUpdate)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var last = _store.LastUpdateCheckUtc;

        if (!force && last > 0 && now - last < UpdateCacheSeconds)
        {
            RestoreKnownUpdate();
            return;
        }

        IsCheckingUpdate = true;
        SetUpdateStatus("Loc.Update.Checking");

        try
        {
            var result = await _updates.CheckAsync(_lifetime.Token).ConfigureAwait(true);

            if (!result.Success)
            {
                SetUpdateStatus("Loc.Update.Failed");
                return;
            }

            _store.RecordUpdateCheck(
                result.Update?.Version ?? string.Empty,
                result.Update?.Url ?? string.Empty,
                now);

            RestoreKnownUpdate();

            if (result.Update is null)
            {
                SetUpdateStatus("Loc.Update.UpToDate");
            }
            else if (HasUpdate)
            {
                SetUpdateStatus("Loc.Update.Available", result.Update.Version);
            }
            else
            {
                SetUpdateStatus("Loc.Update.Dismissed", result.Update.Version);
            }
        }
        catch (OperationCanceledException)
        {
            // The window is closing; the next launch checks again.
        }
        catch
        {
            SetUpdateStatus("Loc.Update.Failed");
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    /// <summary>Hides this exact release; a later one is shown again.</summary>
    public void DismissUpdate()
    {
        if (!HasUpdate)
        {
            return;
        }

        var version = _updateVersion;
        _store.DismissUpdate(version);

        _updateVersion = string.Empty;
        _updateUrl = string.Empty;
        HasUpdate = false;
        SetUpdateStatus("Loc.Update.Dismissed", version);

        OnPropertyChanged(nameof(UpdateVersionText));
        OnPropertyChanged(nameof(UpdateTipText));
        OnPropertyChanged(nameof(HasHiddenUpdate));
        OnPropertyChanged(nameof(UpdateDownloadText));
    }

    /// <summary>Opens the release page in the default browser; never downloads.</summary>
    public void OpenUpdatePage()
    {
        if (!HasUpdate || string.IsNullOrWhiteSpace(_updateUrl))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _updateUrl,
                UseShellExecute = true,
            });
        }
        catch
        {
            SetStatus("Loc.Update.OpenFailed");
        }
    }

    /// <summary>Opens the known release page even if the sidebar card was hidden.</summary>
    public void OpenKnownUpdatePage()
    {
        var url = _store.LatestKnownUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            url = UpdateService.PageUrl;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            SetStatus("Loc.Update.OpenFailed");
        }
    }

    private void RestoreKnownUpdate()
    {
        var known = _store.LatestKnownVersion;
        var visible = UpdateService.IsNewer(known) &&
                      !string.Equals(
                          known,
                          _store.DismissedUpdateVersion,
                          StringComparison.OrdinalIgnoreCase);

        _updateVersion = visible ? known : string.Empty;
        _updateUrl = visible ? _store.LatestKnownUrl : string.Empty;

        if (visible && string.IsNullOrWhiteSpace(_updateUrl))
        {
            _updateUrl = UpdateService.PageUrl;
        }

        HasUpdate = visible;

        OnPropertyChanged(nameof(UpdateVersionText));
        OnPropertyChanged(nameof(UpdateTipText));
        OnPropertyChanged(nameof(HasHiddenUpdate));
        OnPropertyChanged(nameof(UpdateDownloadText));

        // A cached check already has an answer for Settings; leave a running
        // check's "Checking..." text alone.
        if (_updateStatusKey is null && _store.LastUpdateCheckUtc > 0)
        {
            if (visible)
            {
                SetUpdateStatus("Loc.Update.Available", _updateVersion);
            }
            else if (UpdateService.IsNewer(known))
            {
                SetUpdateStatus("Loc.Update.Dismissed", known);
            }
            else
            {
                SetUpdateStatus("Loc.Update.UpToDate");
            }
        }
    }

    private void SetUpdateStatus(string key, params object?[] args)
    {
        _updateStatusKey = key;
        _updateStatusArgs = args;
        UpdateStatusText = Loc.Format(key, args);
    }

    private void ClearUpdateStatus()
    {
        _updateStatusKey = null;
        _updateStatusArgs = null;
        UpdateStatusText = string.Empty;
    }

    /// <summary>Beta source for the cover picker.</summary>
    public bool UseSteamGridDb
    {
        get => _store.UseSteamGridDb;
        set
        {
            if (_store.UseSteamGridDb == value)
            {
                return;
            }

            _store.UseSteamGridDb = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSteamGridDbConfigured));
        }
    }

    public string SteamGridDbApiKey
    {
        get => _store.SteamGridDbApiKey;
        set
        {
            if (string.Equals(_store.SteamGridDbApiKey, value, StringComparison.Ordinal))
            {
                return;
            }

            _store.SteamGridDbApiKey = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSteamGridDbConfigured));
        }
    }

    public bool IsSteamGridDbConfigured =>
        UseSteamGridDb && !string.IsNullOrWhiteSpace(SteamGridDbApiKey);

    public string CoverCacheDirectory => _libraryService.CoverCacheDirectory;

    /// <summary>Steam AppID used by the achievements window, or 0.</summary>
    public int GetSteamAppId(Game game) => _libraryService.GetCachedSteamAppId(game);

    public bool IsCustomCoverCache => _store.CoverCacheDirectory is not null;

    /// <summary>
    /// Moves the cover cache to another folder (existing posters are copied, not
    /// re-downloaded) and reloads the cards from the new location.
    /// </summary>
    public async Task ChangeCoverCacheDirectoryAsync(string? directory)
    {
        if (!_libraryService.TrySetCacheDirectory(directory))
        {
            SetStatus("Loc.Status.CacheFailed");
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

        SetStatus("Loc.Status.CacheUpdated");
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
        SetStatus("Loc.Status.Scanning");

        try
        {
            var outcome = await _libraryService.ScanAsync(
                Folders.ToList(),
                _lifetime.Token);

            var games = _store.Apply(MergeManualGames(outcome.Games));
            ReplaceGames(games);

            _lastScan = DateTime.Now;
            OnPropertyChanged(nameof(LibrarySummary));

            SetStatus(Games.Count == 0
                ? "Loc.Empty.NoGames"
                : "Loc.Status.ScanDone");

            ScanInfo = BuildScanInfo(outcome, games.Count(game => game.Source == GameLibraryService.ManualSource));
            NotifyHiddenChanged();

            await LoadCoversAsync(_lifetime.Token);

            // Goldberg needs a schema before a game can enumerate achievements.
            // It is downloaded after the library is usable, one game at a time.
            _ = ProvisionRepackSchemasAsync(games, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Loc.Status.ScanCanceled");
        }
        catch
        {
            SetStatus("Loc.Status.ScanFailed");
        }
        finally
        {
            IsScanning = false;
        }
    }

    private async Task ProvisionRepackSchemasAsync(
        IEnumerable<Game> games,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var game in games)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var appId = GetSteamAppId(game);
                if (appId <= 0)
                {
                    continue;
                }

                await RepackSupportService.ProvisionAchievementSchemaAsync(
                    game,
                    appId,
                    SteamAchievementsService.Shared,
                    cancellationToken);

                await PlatformAchievementService.ProvisionSchemaAsync(
                    game,
                    appId,
                    SteamAchievementsService.Shared,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The window closed while the schemas were downloading.
        }
        catch
        {
            // A schema is optional: the Steam list still shows the progress.
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
        _store.Hide(game.Id, game.Title);
        Games.Remove(game);
        NotifyHiddenChanged();
    }

    /// <summary>
    /// Deletes the game's record (rename, genre, played time, cached AppID and
    /// poster), hides the id and takes the card out of the library. Hiding is
    /// what keeps a scanned game from reappearing after the next scan: Pikabu
    /// users reported that a plain delete brought the card straight back.
    /// </summary>
    public void DeleteGame(Game game)
    {
        var title = game.Title;

        _store.Hide(game.Id, title);
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
        SetStatus("Loc.Status.Deleted", title);
    }

    /// <summary>Builds the full list shown in the delete confirmation window.</summary>
    public GameUninstallPlan BuildUninstallPlan(Game game)
        => GameUninstallService.Build(game, GetSteamAppId(game));

    /// <summary>
    /// Deletes every file in the uninstall plan and then removes the library
    /// record. The install folder is validated before anything is touched.
    /// </summary>
    public bool UninstallGame(Game game, GameUninstallPlan plan, out string error)
    {
        error = string.Empty;

        var install = plan.Targets.FirstOrDefault(
            target => target.Kind == UninstallTargetKind.Install);

        if (install is not null && !IsSafeGameFolder(install.Path))
        {
            error = Loc.T("Loc.Delete.Unsafe");
            return false;
        }

        var errors = GameUninstallService.Delete(plan);
        if (errors.Count > 0)
        {
            error = Loc.Format(
                "Loc.Delete.Error",
                string.Join(Environment.NewLine, errors));
            return false;
        }

        DeleteGame(game);
        return true;
    }

    private bool IsSafeGameFolder(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath)?
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.IsNullOrWhiteSpace(root) ||
            string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Steam library folders are allowed even under Program Files.
        if (fullPath.IndexOf(
                Path.DirectorySeparatorChar + "steamapps" +
                Path.DirectorySeparatorChar + "common" +
                Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        var critical = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppPaths.DataDirectory,
            AppDomain.CurrentDomain.BaseDirectory,
        };

        foreach (var protectedPath in critical)
        {
            if (string.IsNullOrWhiteSpace(protectedPath))
            {
                continue;
            }

            var normalized = Path.GetFullPath(protectedPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(fullPath, normalized, StringComparison.OrdinalIgnoreCase) ||
                IsChildOf(fullPath, normalized))
            {
                return false;
            }
        }

        foreach (var folder in _store.Folders)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            var normalized = Path.GetFullPath(folder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!string.Equals(fullPath, normalized, StringComparison.OrdinalIgnoreCase) &&
                IsChildOf(fullPath, normalized))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsChildOf(string path, string parent)
        => path.StartsWith(
            parent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Stars or unstars a game; an active star filter updates at once.</summary>
    public void ToggleFavorite(Game game)
    {
        game.IsFavorite = !game.IsFavorite;
        _store.SetFavorite(game.Id, game.IsFavorite);

        if (FavoritesOnly)
        {
            RefreshView();
        }
    }

    /// <summary>Starts the game with its executable or through the Steam client.</summary>
    public bool LaunchGame(Game game)
    {
        try
        {
            var boost = CreateBoostSettings();

            if (!string.IsNullOrWhiteSpace(game.ExecutablePath) &&
                File.Exists(game.ExecutablePath))
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = game.ExecutablePath,
                    Arguments = _store.GetLaunchArguments(game.Id),
                    WorkingDirectory = Path.GetDirectoryName(game.ExecutablePath) ?? string.Empty,
                    UseShellExecute = true,
                });

                if (process is not null && boost.Enabled)
                {
                    GameOptimizationService.ApplyToProcess(process, boost, _cpuTopology);
                    GameOptimizationService.ReapplyAfterDelay(
                        process.Id,
                        boost,
                        _cpuTopology,
                        TimeSpan.FromSeconds(2));
                }

                SetStatus("Loc.Status.Launching", game.Title);
                _store.MarkPlayed(game.Id);

                // Keep the process id: a game started "as administrator" does
                // not expose its module path to a normal launcher, so the name
                // lookup alone would report it as stopped after the grace time.
                _playtime.Track(game, process?.Id ?? 0);
                ApplySessionOptimizations(boost);
                return true;
            }

            if (game.AppId > 0)
            {
                var executable = _libraryService.ResolveLaunchExecutable(game);

                Process.Start(new ProcessStartInfo
                {
                    FileName = $"steam://rungameid/{game.AppId}",
                    UseShellExecute = true,
                });

                if (boost.Enabled)
                {
                    GameOptimizationService.WatchForProcess(executable, boost, _cpuTopology);
                }

                SetStatus("Loc.Status.Launching", game.Title);
                _store.MarkPlayed(game.Id);
                _playtime.Track(game);
                ApplySessionOptimizations(boost);
                return true;
            }

            // An unplugged USB drive or an offline network share keeps its card;
            // the message explains why nothing was started.
            SetStatus(game.IsPathUnavailable && !string.IsNullOrWhiteSpace(game.InstallPath)
                ? "Loc.Status.PathUnavailable"
                : "Loc.Status.NoLaunchFile");
            return false;
        }
        catch
        {
            SetStatus("Loc.Status.LaunchFailed", game.Title);
            return false;
        }
    }

    /// <summary>Returns the launcher and timer to their normal state.</summary>
    public void RestoreLauncherBackground()
    {
        GameOptimizationService.SetLauncherBackground(false);
        TimerResolutionService.Restore();
    }

    private static void ApplySessionOptimizations(GameBoostSettings settings)
    {
        if (settings.Enabled && settings.LauncherBackground)
        {
            GameOptimizationService.SetLauncherBackground(true);
        }

        TimerResolutionService.Apply(
            settings.Enabled ? settings.TimerResolution : GameTimerResolution.Off);
    }

    public string GetLaunchArguments(Game game)
        => _store.GetLaunchArguments(game.Id);

    public void SetLaunchArguments(Game game, string arguments)
        => _store.SetLaunchArguments(game.Id, arguments);

    public void OpenGameFolder(Game game)
    {
        if (string.IsNullOrWhiteSpace(game.InstallPath) ||
            !Directory.Exists(game.InstallPath))
        {
            SetStatus("Loc.Status.FolderMissing");
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
            SetStatus("Loc.Status.OpenFolderFailed");
        }
    }

    /// <summary>Searches Steam for cover variants of the given game/query.</summary>
    public async Task SearchCoversAsync(Game game, string query)
    {
        CoverResults.Clear();

        if (!IsSteamGridDbConfigured)
        {
            CoverStatusText = Loc.T("Loc.Cover.NeedKey");
            return;
        }

        CoverStatusText = Loc.T("Loc.Cover.Searching");

        try
        {
            var results = await _libraryService.SearchSteamGridDbAsync(
                game,
                query,
                SteamGridDbApiKey,
                _lifetime.Token);

            foreach (var candidate in results)
            {
                CoverResults.Add(candidate);
            }

            CoverStatusText = results.Count == 0
                ? Loc.T("Loc.Cover.SgdbNotFound")
                : Loc.Format("Loc.Cover.Found", results.Count);
        }
        catch (OperationCanceledException)
        {
            // Window closing.
        }
        catch
        {
            CoverStatusText = Loc.T("Loc.Cover.Failed");
        }
    }

    /// <summary>Searches SteamGridDB for wide Big Picture banners.</summary>
    public async Task SearchHeroesAsync(Game game, string query)
    {
        HeroResults.Clear();

        if (!IsSteamGridDbConfigured)
        {
            CoverStatusText = Loc.T("Loc.Cover.NeedKey");
            return;
        }

        CoverStatusText = Loc.T("Loc.Cover.Searching");

        try
        {
            var results = await _libraryService.SearchSteamGridDbHeroesAsync(
                game,
                query,
                SteamGridDbApiKey,
                _lifetime.Token);

            foreach (var candidate in results)
            {
                HeroResults.Add(candidate);
            }

            CoverStatusText = results.Count == 0
                ? Loc.T("Loc.Cover.SgdbNotFound")
                : Loc.Format("Loc.Cover.Found", results.Count);
        }
        catch (OperationCanceledException)
        {
            // Window closing.
        }
        catch
        {
            CoverStatusText = Loc.T("Loc.Cover.Failed");
        }
    }

    /// <summary>Downloads and applies a cover chosen from the online results.</summary>
    public async Task<bool> ApplyCoverAsync(Game game, CoverCandidate candidate)
    {
        CoverStatusText = Loc.T("Loc.Cover.Applying");

        try
        {
            var bytes = await _libraryService.DownloadCoverBytesAsync(
                candidate.Url,
                _lifetime.Token);

            var image = bytes is null ? null : CoverImage.LoadBytes(bytes);
            var poster = image is null ? null : CoverImage.CenterCrop(image);
            return SaveCover(game, poster);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            CoverStatusText = Loc.T("Loc.Cover.Failed");
            return false;
        }
    }

    /// <summary>Downloads and applies a wide banner chosen from the online results.</summary>
    public async Task<bool> ApplyHeroAsync(Game game, CoverCandidate candidate)
    {
        CoverStatusText = Loc.T("Loc.Cover.Applying");

        try
        {
            var bytes = await _libraryService.DownloadCoverBytesAsync(
                candidate.Url,
                _lifetime.Token);

            var image = bytes is null ? null : CoverImage.LoadBytes(bytes);
            var hero = image is null ? null : CoverImage.CenterCropHero(image);
            return SaveHero(game, hero);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            CoverStatusText = Loc.T("Loc.Cover.Failed");
            return false;
        }
    }

    /// <summary>Applies a user image as the wide Big Picture banner.</summary>
    public bool ApplyCustomHero(
        Game game,
        BitmapSource source,
        double zoom,
        double offsetX,
        double offsetY,
        double viewportWidth,
        double viewportHeight)
    {
        var hero = CoverImage.CropToHero(
            source,
            zoom,
            offsetX,
            offsetY,
            viewportWidth,
            viewportHeight);

        return SaveHero(game, hero);
    }

    /// <summary>Applies a user image using the pan/zoom from the crop dialog.</summary>
    public bool ApplyCustomCover(
        Game game,
        BitmapSource source,
        double zoom,
        double offsetX,
        double offsetY,
        double viewportWidth,
        double viewportHeight)
    {
        var poster = CoverImage.CropToPoster(
            source,
            zoom,
            offsetX,
            offsetY,
            viewportWidth,
            viewportHeight);

        return SaveCover(game, poster);
    }

    /// <summary>Removes the user cover and loads the downloaded one again.</summary>
    public async Task ResetCoverAsync(Game game)
    {
        _store.RemoveCustomCover(game.Id);
        game.Cover = null;
        await LoadCoverAsync(game, _lifetime.Token, DownloadCovers);
    }

    /// <summary>Removes the user banner and restores the store artwork.</summary>
    public void ResetHero(Game game)
    {
        if (_store.GetCustomHeroPath(game.Id) is null)
        {
            CoverStatusText = Loc.T("Loc.Cover.Applied");
            return;
        }

        _store.RemoveCustomHero(game.Id);
        _libraryService.InvalidateHeroArtwork(game.Id);
        CoverStatusText = Loc.T("Loc.Cover.Applied");
    }

    private bool SaveHero(Game game, BitmapSource? hero)
    {
        if (hero is null)
        {
            CoverStatusText = Loc.T("Loc.Cover.Failed");
            return false;
        }

        var jpeg = CoverImage.ToJpeg(hero);
        var path = jpeg is null ? null : _store.SaveCustomHero(game.Id, jpeg);

        if (string.IsNullOrWhiteSpace(path))
        {
            CoverStatusText = Loc.T("Loc.Cover.Failed");
            return false;
        }

        CoverStatusText = Loc.T("Loc.Cover.Applied");
        return true;
    }

    private bool SaveCover(Game game, BitmapSource? poster)
    {
        if (poster is null)
        {
            CoverStatusText = Loc.T("Loc.Cover.Failed");
            return false;
        }

        var jpeg = CoverImage.ToJpeg(poster);
        var path = jpeg is null ? null : _store.SaveCustomCover(game.Id, jpeg);
        var loaded = path is null ? null : CoverImage.LoadFile(path);

        if (loaded is null)
        {
            CoverStatusText = Loc.T("Loc.Cover.Failed");
            return false;
        }

        game.Cover = loaded;
        CoverStatusText = Loc.T("Loc.Cover.Applied");
        return true;
    }

    public async Task RestoreHiddenAsync()
    {
        _store.RestoreHidden();
        NotifyHiddenChanged();
        await RescanAsync();
    }

    /// <summary>Brings back one hidden game without touching the rest of the list.</summary>
    public async Task RestoreHiddenGameAsync(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return;
        }

        _store.Unhide(gameId);
        NotifyHiddenChanged();
        await RescanAsync();
    }

    /// <summary>
    /// Adds a single game (a folder or an .exe) to the library. The rest of the
    /// library is not rescanned; covers and genres are fetched as usual.
    /// </summary>
    public async Task<bool> AddSingleGameAsync(string path)
    {
        Game? game;
        try
        {
            // The folder probe walks the disk (up to eight levels) and must not
            // freeze the window on a slow or temporarily unavailable drive.
            SetStatus("Loc.Status.Searching");
            game = await Task.Run(
                () => GameLibraryService.CreateManualGame(path, _lifetime.Token),
                _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (game is null)
        {
            SetStatus("Loc.Status.NoExeFound");
            return false;
        }

        var existing = Games.FirstOrDefault(item => SameGamePath(item, game));
        if (existing is not null)
        {
            SetStatus("Loc.Status.AlreadyInLibrary", existing.Title);
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
        SetStatus("Loc.Status.Added", game.Title);
        return true;
    }

    /// <summary>
    /// Points an existing manual entry at a new folder (the drive letter
    /// changed, the game was moved). The id is kept, so the custom title, the
    /// hidden state and the played time stay attached to the same card.
    /// </summary>
    public async Task<bool> ReassignGameFolderAsync(Game game, string folder)
    {
        Game? rebuilt;
        try
        {
            SetStatus("Loc.Status.Searching");
            rebuilt = await Task.Run(
                () => GameLibraryService.CreateManualGame(folder, _lifetime.Token),
                _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (rebuilt is null ||
            string.IsNullOrWhiteSpace(rebuilt.InstallPath) ||
            string.IsNullOrWhiteSpace(rebuilt.ExecutablePath))
        {
            SetStatus("Loc.Status.NoExeFound");
            return false;
        }

        _store.UpdateManualGame(new ManualGameEntry
        {
            Id = game.Id,
            Title = game.Title,
            InstallPath = rebuilt.InstallPath,
            ExecutablePath = rebuilt.ExecutablePath,
        });

        await RescanAsync();
        SetStatus("Loc.Status.Added", game.Title);
        return true;
    }

    /// <summary>Switches the group headers for the current session.</summary>
    public async Task SetGroupingAsync(LibraryGrouping grouping)
    {
        if (_grouping == grouping)
        {
            return;
        }

        _grouping = grouping;
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
            return Loc.T("Loc.Scan.NothingFound");
        }

        var parts = new List<string>
        {
            $"{GameLibraryService.SteamSource}: {CountOf(outcome, GameLibraryService.SteamSource)}",
            $"{GameLibraryService.EpicSource}: {CountOf(outcome, GameLibraryService.EpicSource)}",
            $"{GameLibraryService.GogSource}: {CountOf(outcome, GameLibraryService.GogSource)}",
            Loc.Format("Loc.Scan.FolderCount", CountOf(outcome, GameLibraryService.FolderSource)),
        };

        if (manualCount > 0)
        {
            parts.Add($"{Loc.SourceName(GameLibraryService.ManualSource)}: {manualCount}");
        }

        return string.Join(" · ", parts);
    }

    private static int CountOf(ScanOutcome outcome, string source)
        => outcome.SourceCounts.TryGetValue(source, out var count) ? count : 0;

    private async Task LoadCoversAsync(CancellationToken cancellationToken)
    {
        // A user-picked cover is loaded even when online cover downloads are
        // turned off; only the fallback download is skipped.
        var tasks = Games
            .Where(game => game.Cover is null)
            .Select(game => LoadCoverAsync(game, cancellationToken, DownloadCovers))
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

    private async Task LoadCoverAsync(
        Game game,
        CancellationToken cancellationToken,
        bool allowDownload = true)
    {
        if (game.Cover is not null)
        {
            return;
        }

        try
        {
            var customCover = _store.GetCustomCoverPath(game.Id);
            if (customCover is not null)
            {
                game.Cover = await Task.Run(
                    () => CoverImage.LoadFile(customCover),
                    cancellationToken);

                if (game.Cover is not null)
                {
                    return;
                }
            }

            if (!allowDownload)
            {
                return;
            }

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
            // A record without paths cannot be repaired or launched; only that
            // case is dropped.
            if (string.IsNullOrWhiteSpace(entry.InstallPath) ||
                string.IsNullOrWhiteSpace(entry.ExecutablePath))
            {
                _store.RemoveManualGame(entry.Id);
                continue;
            }

            var pathAvailable = Directory.Exists(entry.InstallPath) &&
                                File.Exists(entry.ExecutablePath);

            // An unplugged USB drive or an offline network share must not erase
            // the user's record: the card stays and explains why it cannot run.
            if (pathAvailable)
            {
                // Older builds used the executable name and the deepest
                // technical folder. Rebuild the readable title/root from the
                // executable and persist the repaired values, keeping the old
                // id so renames and hidden state still match.
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
                LibraryGrouping.Genre => Loc.GenreName(_store.GetGenre(game.Id)),
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
        OnPropertyChanged(nameof(VisibleCountText));
        OnPropertyChanged(nameof(HasVisibleGames));
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(IsNarrowed));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(LibrarySummary));
    }

    private bool FilterGame(object item)
    {
        if (item is not Game game)
        {
            return false;
        }

        if (FavoritesOnly && !game.IsFavorite)
        {
            return false;
        }

        if (!IsFiltered)
        {
            return true;
        }

        var query = _searchQuery.Trim();

        return Matches(game.Title) ||
               Matches(game.Source) ||
               Matches(game.Group) ||
               Matches(game.InstallPath);

        bool Matches(string? value)
            => !string.IsNullOrWhiteSpace(value) &&
               value.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
    }

    /// <summary>
    /// Steam genres are fetched once per game and remembered in library.json.
    /// Group headers appear immediately and fill in as answers arrive.
    /// </summary>
    private async Task LoadGenresAsync(bool showStatus = true)
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
        var changed = false;

        if (showStatus)
        {
            SetStatus("Loc.Status.GenresLoading", 0, total);
        }

        try
        {
            var tasks = missing.Select(async game =>
            {
                var genre = await _libraryService.GetGenreAsync(game, _lifetime.Token);
                if (!string.IsNullOrWhiteSpace(genre))
                {
                    _store.SetGenre(game.Id, genre);
                    changed = true;
                }

                finished++;
                if (showStatus)
                {
                    SetStatus("Loc.Status.GenresLoading", finished, total);
                }
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

        if (changed)
        {
            GenresUpdated?.Invoke();
        }

        if (_grouping == LibraryGrouping.Genre)
        {
            ApplyGroups();
            RefreshView();

            if (showStatus)
            {
                SetStatus("Loc.Status.GenresDone");
            }
        }
    }

    /// <summary>Fetches genres without touching the library status line (Big Picture).</summary>
    public Task EnsureGenresAsync() => LoadGenresAsync(showStatus: false);

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
        RefreshAchievementGames();

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
        RefreshHiddenGames();
        OnPropertyChanged(nameof(HiddenCount));
        OnPropertyChanged(nameof(HasHidden));
        OnPropertyChanged(nameof(HiddenSummary));
    }

    /// <summary>Rebuilds the per-item list shown in Settings.</summary>
    private void RefreshHiddenGames()
    {
        HiddenGames.Clear();

        foreach (var item in _store.Hidden
                     .Select(id => new HiddenGameItem(
                         id,
                         ResolveHiddenTitle(id),
                         ResolveHiddenSource(id)))
                     .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            HiddenGames.Add(item);
        }
    }

    private string ResolveHiddenTitle(string gameId)
        => _store.GetHiddenTitle(gameId)
           ?? _store.GetCustomTitle(gameId)
           ?? FallbackTitleFromId(gameId);

    private static string FallbackTitleFromId(string gameId)
    {
        var separator = gameId.IndexOf(':');
        if (separator < 0 || separator == gameId.Length - 1)
        {
            return gameId;
        }

        var kind = gameId.Substring(0, separator).ToLowerInvariant();
        var value = gameId.Substring(separator + 1).Trim();

        return kind switch
        {
            "folder" or "manual" => string.IsNullOrWhiteSpace(value)
                ? gameId
                : Path.GetFileName(value.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)),
            "steam" => $"Steam · {value}",
            "epic" => $"Epic · {value}",
            "gog" => $"GOG · {value}",
            _ => gameId,
        };
    }

    private static string ResolveHiddenSource(string gameId)
    {
        var separator = gameId.IndexOf(':');
        var kind = separator < 0 ? gameId : gameId.Substring(0, separator);
        var value = separator < 0 ? string.Empty : gameId.Substring(separator + 1).Trim();

        return kind.ToLowerInvariant() switch
        {
            "folder" => string.IsNullOrWhiteSpace(value)
                ? Loc.SourceName(GameLibraryService.FolderSource)
                : $"{Loc.SourceName(GameLibraryService.FolderSource)} · {value}",
            "manual" => string.IsNullOrWhiteSpace(value)
                ? Loc.SourceName(GameLibraryService.ManualSource)
                : $"{Loc.SourceName(GameLibraryService.ManualSource)} · {value}",
            "steam" => GameLibraryService.SteamSource,
            "epic" => GameLibraryService.EpicSource,
            "gog" => GameLibraryService.GogSource,
            _ => kind,
        };
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

    /// <summary>Stores the resource key so a language switch re-renders the text.</summary>
    private void SetStatus(string key, params object?[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        StatusText = Loc.Format(key, args);
    }

    private void OnGameStopped(string gameId)
        => GameStopped?.Invoke(gameId);

    private void OnLanguageChanged()
    {
        ApplyGroups();
        RefreshView();
        RefreshAchievementGames();

        foreach (var game in Games)
        {
            game.RefreshLocalization();
        }

        if (_statusKey is not null)
        {
            StatusText = Loc.Format(_statusKey, _statusArgs ?? Array.Empty<object>());
        }

        if (_updateStatusKey is not null)
        {
            UpdateStatusText = Loc.Format(_updateStatusKey, _updateStatusArgs ?? Array.Empty<object>());
        }

        OnPropertyChanged(nameof(UpdateVersionText));
        OnPropertyChanged(nameof(UpdateTipText));
        OnPropertyChanged(nameof(HasHiddenUpdate));
        OnPropertyChanged(nameof(UpdateDownloadText));
        OnPropertyChanged(nameof(CpuSummary));
        OnPropertyChanged(nameof(CpuModeHint));

        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(GamesCountText));
        OnPropertyChanged(nameof(VisibleCountText));
        OnPropertyChanged(nameof(VersionSummary));
        OnPropertyChanged(nameof(HiddenSummary));
        OnPropertyChanged(nameof(FoldersSummary));
        OnPropertyChanged(nameof(LibrarySummary));
        RefreshHiddenGames();
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

        Loc.Changed -= OnLanguageChanged;
        _playtime.PlaytimeChanged -= OnPlaytimeChanged;
        _playtime.GameStopped -= OnGameStopped;
        _playtime.Dispose();
        GameOptimizationService.SetLauncherBackground(false);
        TimerResolutionService.Restore();
        _libraryService.Dispose();
    }
}
