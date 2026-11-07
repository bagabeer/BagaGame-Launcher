using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using BagaGame.Models;
using BagaGame.Services;
using BagaGame.Services.Input;
using BagaGame.ViewModels;
using BagaGame.Views;
using Microsoft.Win32;

namespace BagaGame;

public partial class MainWindow : BagaGame.Controls.BagaWindow
{
    private readonly MainViewModel _viewModel;
    private readonly LibraryStore _store;
    private Game? _renameTarget;
    private Game? _launchArgsTarget;
    private Game? _deleteGameTarget;
    private GameUninstallPlan? _deletePlan;
    private TrayIcon? _tray;
    private BigPictureWindow? _bigPicture;
    private DispatcherTimer? _bigPictureLaunchWatch;
    private Process? _bigPictureLaunchedProcess;
    private Game? _bigPictureGame;
    private readonly GamepadService _gamepadWatcher;
    private DateTime _gamepadWatchStartedUtc;
    private bool _leavingBigPicture;
    private DispatcherTimer? _gamePlacementWatch;
    private readonly SteamAchievementsService _steamAchievements = new();
    private readonly LocalAchievementsService _localAchievements = new();
    private SteamAchievementNotifier? _achievementNotifier;
    private bool _syncingGrouping;
    private Game? _coverTarget;
    private BitmapSource? _cropSource;
    private bool _cropDragging;
    private Point _cropDragStart;
    private double _cropStartX;
    private double _cropStartY;
    private double _cropZoom = 1;

    public MainWindow()
        : this(new LibraryStore())
    {
    }

    public MainWindow(LibraryStore store)
    {
        InitializeComponent();

        _store = store;

        // Keep the designed size, but never open larger than the usable desktop
        // (important on 125–200% DPI laptops).
        var workArea = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(Width, workArea.Width - 24));
        Height = Math.Max(MinHeight, Math.Min(Height, workArea.Height - 24));

        // Center on the primary screen: Windows otherwise may place the window
        // on another monitor (or off-screen) when the splash owned the
        // foreground during startup.
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2;

        _viewModel = new MainViewModel(
            new GameLibraryService(store.CoverCacheDirectory),
            store);
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.GameStopped += ViewModel_GameStopped;
        DataContext = _viewModel;

        // Setting IsChecked before Loaded does not trigger a theme swap; the
        // startup theme was already applied by App.OnStartup.
        ThemeToggle.IsChecked = store.IsLightTheme;

        // Automatic Big Picture: a slow poll that reacts only while this window
        // is active, so a controller connected elsewhere never opens the mode.
        _gamepadWatcher = new GamepadService(TimeSpan.FromMilliseconds(250));
        _gamepadWatcher.ConnectedChanged += OnGamepadWatcherConnection;

        Activated += (_, _) => UpdateGamepadWatcher();
        Deactivated += (_, _) => _gamepadWatcher.Stop();
        StateChanged += (_, _) => UpdateGamepadWatcher();

        Loaded += MainWindow_Loaded;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;

        // Restore the saved layout and make the toggle buttons match it.
        GridViewButton.IsChecked = _store.GridView;
        ListViewButton.IsChecked = !_store.GridView;
        ApplyViewMode(_store.GridView);
        SelectGrouping(_viewModel.Grouping);

        // The checked radio button mirrors the language applied at startup.
        LanguageRuButton.IsChecked = Loc.Current == AppLanguage.Russian;
        LanguageEnButton.IsChecked = Loc.Current == AppLanguage.English;

        // Performance controls follow the capabilities of this PC. Unsupported
        // modes stay visible but disabled, so the same settings page is usable
        // on every machine.
        SyncPerformanceControls();

        // The release check runs next to the scan and never blocks it.
        _ = _viewModel.CheckForUpdatesAsync();

        // Covers stream in after the window is already visible.
        await _viewModel.InitializeAsync();

        ScheduleCleanup();
    }

    /// <summary>Runs the safe cache cleanup at most once a day, off the UI thread.</summary>
    private async void ScheduleCleanup()
    {
        var last = _store.LastCleanupUtc;
        if (last.HasValue && DateTime.UtcNow - last.Value < TimeSpan.FromHours(24))
        {
            return;
        }

        try
        {
            await CleanupService.RunAsync(CancellationToken.None);
            _store.LastCleanupUtc = DateTime.UtcNow;
        }
        catch
        {
            // Cleanup is optional and never blocks the launcher.
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsScanning))
        {
            UpdateScanAnimation();
        }
        else if (e.PropertyName == nameof(MainViewModel.BigPictureOnGamepad))
        {
            UpdateGamepadWatcher();
        }
        else if (e.PropertyName is nameof(MainViewModel.CpuMode)
                 or nameof(MainViewModel.GamePriority))
        {
            SyncPerformanceControls();
        }
    }

    private void SyncPerformanceControls()
    {
        // Show only the modes this CPU actually supports. A non-hybrid CPU
        // gets a single Auto segment instead of greyed-out P/E buttons.
        CpuPerformanceButton.Visibility = _viewModel.CanUsePerformanceCores
            ? Visibility.Visible
            : Visibility.Collapsed;
        CpuEfficiencyButton.Visibility = _viewModel.CanUseEfficiencyCores
            ? Visibility.Visible
            : Visibility.Collapsed;
        CpuL3Button.Visibility = _viewModel.CanSelectL3Domain
            ? Visibility.Visible
            : Visibility.Collapsed;

        var visibleModes = 1 +
            (_viewModel.CanUsePerformanceCores ? 1 : 0) +
            (_viewModel.CanUseEfficiencyCores ? 1 : 0) +
            (_viewModel.CanSelectL3Domain ? 1 : 0);

        CpuModeGrid.Columns = visibleModes;
        CpuModeGrid.Width = visibleModes * 70;

        CpuAutoButton.IsChecked = _viewModel.CpuMode == CpuOptimizationMode.Auto;
        CpuPerformanceButton.IsChecked = _viewModel.CpuMode == CpuOptimizationMode.PerformanceCores;
        CpuEfficiencyButton.IsChecked = _viewModel.CpuMode == CpuOptimizationMode.EfficiencyCores;
        CpuL3Button.IsChecked = _viewModel.CpuMode == CpuOptimizationMode.PreferredL3Domain;

        PriorityNormalButton.IsChecked = _viewModel.GamePriority == GameProcessPriority.Normal;
        PriorityAboveButton.IsChecked = _viewModel.GamePriority == GameProcessPriority.AboveNormal;
        PriorityHighButton.IsChecked = _viewModel.GamePriority == GameProcessPriority.High;

        TimerOffButton.IsChecked = _viewModel.TimerResolution == GameTimerResolution.Off;
        TimerOneButton.IsChecked = _viewModel.TimerResolution == GameTimerResolution.OneMs;
        TimerHalfButton.IsChecked = _viewModel.TimerResolution == GameTimerResolution.HalfMs;
    }

    /// <summary>
    /// Starts or stops the automatic Big Picture watcher. It only runs while
    /// this window is the active one.
    /// </summary>
    private void UpdateGamepadWatcher()
    {
        var active = IsVisible && IsActive && WindowState != WindowState.Minimized;
        var bigPictureClosed = _bigPicture is null || !_bigPicture.IsVisible;

        if (_store.BigPictureOnGamepad && active && bigPictureClosed)
        {
            _gamepadWatchStartedUtc = DateTime.UtcNow;
            _gamepadWatcher.Start();
            return;
        }

        _gamepadWatcher.Stop();
    }

    private void OnGamepadWatcherConnection(bool connected)
    {
        if (!connected ||
            !_store.BigPictureOnGamepad ||
            !IsActive ||
            WindowState == WindowState.Minimized ||
            _bigPicture is { IsVisible: true })
        {
            return;
        }

        // A controller that was already attached when the watcher started is a
        // baseline, not a connection event.
        if ((DateTime.UtcNow - _gamepadWatchStartedUtc).TotalMilliseconds < 800)
        {
            return;
        }

        OpenBigPicture();
    }

    private void UpdateScanAnimation()
    {
        if (_viewModel.IsScanning)
        {
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(850))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            RefreshRotation.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
        else
        {
            RefreshRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            RefreshRotation.Angle = 0;
        }
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RescanAsync();
    }

    private async void ScanNow_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RescanAsync();

        // Show the freshly scanned library.
        LibraryNav.IsChecked = true;
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = FolderPicker.Pick(this, Loc.T("Loc.Dialog.GamesFolder"));
        if (folder is null)
        {
            return;
        }

        await _viewModel.AddFolderAsync(folder);
        LibraryNav.IsChecked = true;
    }

    private async void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string folder)
        {
            return;
        }

        await _viewModel.RemoveFolderAsync(folder);
    }

    private async void RestoreHidden_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RestoreHiddenAsync();
    }

    private async void RestoreHiddenGame_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string gameId)
        {
            await _viewModel.RestoreHiddenGameAsync(gameId);
        }
    }

    // ------------------------------------------------------------------
    // Add a single game
    // ------------------------------------------------------------------

    private void AddGame_Click(object sender, RoutedEventArgs e)
    {
        AddGameOverlay.Visibility = Visibility.Visible;
    }

    private async void AddGameFolder_Click(object sender, RoutedEventArgs e)
    {
        CloseAddGameOverlay();

        var folder = FolderPicker.Pick(this, Loc.T("Loc.Dialog.GameFolder"));
        if (folder is null)
        {
            return;
        }

        await AddGameAsync(folder);
    }

    private async void AddGameExe_Click(object sender, RoutedEventArgs e)
    {
        CloseAddGameOverlay();

        var dialog = new OpenFileDialog
        {
            Title = Loc.T("Loc.Dialog.GameExe"),
            Filter = Loc.T("Loc.Dialog.ExeFilter"),
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await AddGameAsync(dialog.FileName);
    }

    private void AddGameCancel_Click(object sender, RoutedEventArgs e) => CloseAddGameOverlay();

    private void AddGameOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, AddGameOverlay))
        {
            CloseAddGameOverlay();
        }
    }

    private void CloseAddGameOverlay() => AddGameOverlay.Visibility = Visibility.Collapsed;

    private async Task AddGameAsync(string path)
    {
        if (!await _viewModel.AddSingleGameAsync(path))
        {
            return;
        }

        LibraryNav.IsChecked = true;

        if (_viewModel.IsFiltered)
        {
            _viewModel.SearchQuery = string.Empty;
        }

        ScrollHost.ScrollToTop();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SearchQuery = string.Empty;
        SearchBox.Focus();
    }

    /// <summary>The whole update card opens the release page in the browser.</summary>
    private void UpdateCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _viewModel.OpenUpdatePage();
        e.Handled = true;
    }

    private void DismissUpdate_Click(object sender, RoutedEventArgs e)
        => _viewModel.DismissUpdate();

    private void CheckUpdates_Click(object sender, RoutedEventArgs e)
        => _ = _viewModel.CheckForUpdatesAsync(force: true);

    // ------------------------------------------------------------------
    // Launch / context menu
    // ------------------------------------------------------------------

    private void GameCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Game game)
        {
            LaunchGameFromLibrary(game);
        }
    }

    private void LaunchGame_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is Game game)
        {
            LaunchGameFromLibrary(game);
        }
    }

    /// <summary>
    /// Starts a game from the library window and makes sure its window lands
    /// on the monitor the launcher is on, not on the primary display.
    /// </summary>
    private void LaunchGameFromLibrary(Game game)
    {
        var monitor = GetMonitorBounds(this);
        StopGamePlacementWatch();

        if (!_viewModel.LaunchGame(game))
        {
            return;
        }

        if (monitor is { } target)
        {
            StartGamePlacementWatch(game, target);
        }

        HideInTray();
        StartAchievementNotifications(game);
    }

    private void StartAchievementNotifications(Game game)
    {
        var appId = _viewModel.GetSteamAppId(game);
        if (appId <= 0)
        {
            return;
        }

        RepackSupportService.EnsureProvisioned(game, appId);
        PlatformAchievementService.PrepareGame(game);

        if (_viewModel.AchievementScanNotice)
        {
            ShowAchievementScanNotice(game, appId);
        }

        if (_achievementNotifier is null)
        {
            _achievementNotifier = new SteamAchievementNotifier(
                _steamAchievements,
                _localAchievements,
                (title, text, icon) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    var toast = new AchievementToastWindow(title, text, icon);
                    toast.Show();
                })));
            _achievementNotifier.AchievementShown += OnAchievementShown;
        }

        _achievementNotifier.Start(game, appId);
    }

    private void OnAchievementShown(Game game) => _viewModel.MarkAchievementVerified(game);

    /// <summary>
    /// One toast before the game starts: which emulator was found and whether
    /// any achievements are already recorded. This makes a silent repack
    /// obvious before the user spends time playing it.
    /// </summary>
    private void ShowAchievementScanNotice(Game game, int appId)
    {
        try
        {
            var support = AchievementSupportService.Describe(game, appId, false);

            var text = support is null || string.IsNullOrWhiteSpace(support.ScanText)
                ? Loc.T("Loc.Achievements.ScanNone")
                : support.ScanText;

            var toast = new AchievementToastWindow(
                Loc.T("Loc.Achievements.ScanTitle"),
                text,
                null,
                compact: true);
            toast.Show();
        }
        catch
        {
            // The notice is optional.
        }
    }

    private void StopAchievementNotifications() => _achievementNotifier?.Stop();

    private void RefreshAchievements_Click(object sender, RoutedEventArgs e)
        => _viewModel.RefreshAchievementGames();

    /// <summary>
    /// The ListBox lives inside an overlay ScrollViewer; without forwarding the
    /// wheel event the ListBox swallows it and the list stops scrolling.
    /// </summary>
    private void AchievementGames_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (AchievementGamesScroll is null)
        {
            return;
        }

        AchievementGamesScroll.ScrollToVerticalOffset(
            AchievementGamesScroll.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    private void SteamAchievements_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not Game game)
        {
            return;
        }

        var appId = _viewModel.GetSteamAppId(game);
        if (appId <= 0)
        {
            MessageBox.Show(
                this,
                Loc.T("Loc.Achievements.NoAppId"),
                Loc.T("Loc.Achievements.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        RepackSupportService.EnsureProvisioned(game, appId);
        PlatformAchievementService.PrepareGame(game);

        new SteamAchievementsWindow(game, appId) { Owner = this }.ShowDialog();
    }

    private void EmulatorsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedAchievementGame?.Game is Game game)
        {
            OpenEmulators(game);
        }
    }

    private void OpenEmulators(Game game)
    {
        var appId = _viewModel.GetSteamAppId(game);
        new EmulatorsWindow(game, appId, _viewModel.Games) { Owner = this }.ShowDialog();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is Game game)
        {
            _viewModel.OpenGameFolder(game);
        }
    }

    private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Game game)
        {
            _viewModel.ToggleFavorite(game);
        }
    }

    // ------------------------------------------------------------------
    // Big Picture (fullscreen television mode)
    // ------------------------------------------------------------------
    private void BigPicture_Click(object sender, RoutedEventArgs e) => OpenBigPicture();

    /// <summary>
    /// Opens the fullscreen mode. The library window is hidden but stays alive:
    /// the view model, the playtime tracker and the tray icon live in it.
    /// </summary>
    public void OpenBigPicture()
    {
        if (_bigPicture is { IsVisible: true } visible)
        {
            visible.Activate();
            return;
        }

        if (_bigPicture is null)
        {
            var window = new BigPictureWindow(_viewModel, _store) { Owner = this };
            window.ExitRequested += (_, _) => ExitBigPicture(null);
            window.NavigateRequested += page => ExitBigPicture(page);
            window.PlayRequested += LaunchFromBigPicture;
            _bigPicture = window;
        }

        _leavingBigPicture = false;
        BigPictureTransition.Enter(this, _bigPicture, _store.BigPictureSounds);
    }

    /// <summary>Fades Big Picture out and brings the library back.</summary>
    public void ExitBigPicture(string? page)
    {
        if (_leavingBigPicture)
        {
            return;
        }

        if (_bigPicture is not { } window)
        {
            if (page is not null)
            {
                OpenMainPage(page);
            }

            return;
        }

        _leavingBigPicture = true;

        BigPictureTransition.Exit(window, this, _store.BigPictureSounds, () =>
        {
            window.CloseApproved();
            _bigPicture = null;
            _leavingBigPicture = false;

            if (page is not null)
            {
                OpenMainPage(page);
            }
        });
    }

    private void OpenMainPage(string page)
    {
        switch (page)
        {
            case "Achievements":
                AchievementsNav.IsChecked = true;
                break;

            case "Scan":
                ScanNav.IsChecked = true;
                break;

            case "Settings":
                SettingsNav.IsChecked = true;
                break;

            default:
                LibraryNav.IsChecked = true;
                break;
        }
    }

    private void LaunchFromBigPicture(Game game)
    {
        // Remember the Big Picture monitor before the game starts: its window
        // must land there, not on the primary display.
        var monitor = _bigPicture is { } window ? GetMonitorBounds(window) : null;
        StopGamePlacementWatch();

        if (!_viewModel.LaunchGame(game))
        {
            return;
        }

        _bigPictureGame = game;

        // The tray icon appears right away so the app stays reachable, but no
        // balloon: Big Picture itself covers the screen until the game window
        // takes over.
        _tray ??= new TrayIcon(RestoreFromTray, ExitFromTray);
        _tray.RefreshTexts();
        _tray.Visible = true;

        StartBigPictureLaunchWatch();

        if (monitor is { } target)
        {
            StartGamePlacementWatch(game, target);
        }

        StartAchievementNotifications(game);
    }

    private static Rect? GetMonitorBounds(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var bounds = System.Windows.Forms.Screen.FromHandle(handle).Bounds;
            return new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Pulls the started game onto the Big Picture monitor. The game itself
    /// picks the primary display, so its window is moved right after it
    /// appears; a few retries catch engines that recreate or reposition the
    /// window while loading.
    /// </summary>
    private void StartGamePlacementWatch(Game game, Rect target)
    {
        var started = DateTime.UtcNow;
        var watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _gamePlacementWatch = watch;

        watch.Tick += (_, _) =>
        {
            if ((DateTime.UtcNow - started).TotalSeconds > 20)
            {
                StopGamePlacementWatch();
                return;
            }

            var process = FindGameProcessWindow(game);
            if (process is null)
            {
                return;
            }

            try
            {
                PlaceGameWindow(process.MainWindowHandle, target);
            }
            finally
            {
                process.Dispose();
            }
        };

        watch.Start();
    }

    private void StopGamePlacementWatch()
    {
        _gamePlacementWatch?.Stop();
        _gamePlacementWatch = null;
    }

    private static void PlaceGameWindow(IntPtr handle, Rect target)
    {
        try
        {
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out var window))
            {
                return;
            }

            var source = System.Windows.Forms.Screen.FromHandle(handle).Bounds;
            if (source.Left == (int)target.Left && source.Top == (int)target.Top)
            {
                return;
            }

            if (window.Right <= window.Left || window.Bottom <= window.Top)
            {
                return;
            }

            const uint noSize = 0x0001;
            const uint noZOrder = 0x0004;
            const uint noActivate = 0x0010;

            var width = window.Right - window.Left;
            var height = window.Bottom - window.Top;

            // A borderless or fullscreen window covers its monitor exactly:
            // fill the target monitor. A smaller window is centered on it; an
            // oversized window is shrunk to fit.
            var fillsMonitor =
                Math.Abs(window.Left - source.Left) <= 2 &&
                Math.Abs(window.Top - source.Top) <= 2 &&
                Math.Abs(window.Right - source.Right) <= 2 &&
                Math.Abs(window.Bottom - source.Bottom) <= 2;

            if (fillsMonitor ||
                width > target.Width + 2 ||
                height > target.Height + 2)
            {
                SetWindowPos(
                    handle,
                    IntPtr.Zero,
                    (int)target.Left,
                    (int)target.Top,
                    (int)target.Width,
                    (int)target.Height,
                    noZOrder | noActivate);
                return;
            }

            SetWindowPos(
                handle,
                IntPtr.Zero,
                (int)target.Left + ((int)target.Width - width) / 2,
                (int)target.Top + ((int)target.Height - height) / 2,
                0,
                0,
                noSize | noZOrder | noActivate);
        }
        catch
        {
            // The window may have been recreated between the checks; the next
            // tick tries again.
        }
    }

    /// <summary>
    /// Keeps Big Picture on screen while the game starts and hides it only when
    /// another window (the game or its launcher) takes the foreground. Hiding it
    /// immediately would flash the desktop.
    /// </summary>
    private void StartBigPictureLaunchWatch()
    {
        StopBigPictureLaunchWatch();

        var started = DateTime.UtcNow;
        var watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _bigPictureLaunchWatch = watch;

        watch.Tick += (_, _) =>
        {
            // Hide Big Picture only when the game window really exists. A
            // launcher that steals the foreground for a moment must not reveal
            // the desktop.
            var gameProcess = FindGameProcessWindow(_bigPictureGame);
            if (gameProcess is not null)
            {
                StopBigPictureLaunchWatch();
                _bigPicture?.Hide();
                WatchProcessExit(gameProcess);
                return;
            }

            if ((DateTime.UtcNow - started).TotalSeconds > 20)
            {
                StopBigPictureLaunchWatch();
                var foreground = GetForegroundWindow();
                _bigPicture?.Hide();
                WatchForegroundProcess(foreground);
            }
        };

        watch.Start();
    }

    /// <summary>The first process of the launched game that owns a visible window.</summary>
    private static Process? FindGameProcessWindow(Game? game)
    {
        if (game is null)
        {
            return null;
        }

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.MainWindowHandle == IntPtr.Zero)
                {
                    process.Dispose();
                    continue;
                }

                string? path = null;

                try
                {
                    path = process.MainModule?.FileName;
                }
                catch
                {
                    // An elevated process: it is not ours.
                }

                if (path is null)
                {
                    process.Dispose();
                    continue;
                }

                var matches = !string.IsNullOrWhiteSpace(game.ExecutablePath)
                    ? string.Equals(
                        Path.GetFullPath(path),
                        Path.GetFullPath(game.ExecutablePath),
                        StringComparison.OrdinalIgnoreCase)
                    : !string.IsNullOrWhiteSpace(game.InstallPath) &&
                      IsInside(game.InstallPath, path);

                if (matches)
                {
                    return process;
                }
            }
            catch
            {
                // Skip this process and keep looking.
            }

            process.Dispose();
        }

        return null;
    }

    private void WatchProcessExit(Process process)
    {
        try
        {
            _bigPictureLaunchedProcess = process;
            process.EnableRaisingEvents = true;

            process.Exited += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(_bigPictureLaunchedProcess, process))
                {
                    return;
                }

                StopBigPictureGameWatch();
                ViewModel_GameStopped(_bigPictureGame?.Id ?? string.Empty);
            }));

            if (process.HasExited)
            {
                StopBigPictureGameWatch();
            }
        }
        catch
        {
            // The tracker still reports the close shortly after.
        }
    }

    /// <summary>
    /// Watches the process that took over the screen. Its exit is the fastest
    /// reliable signal that the game closed, so Big Picture returns at once
    /// instead of waiting for the three-second playtime poll.
    /// </summary>
    private void WatchForegroundProcess(IntPtr hwnd)
    {
        StopBigPictureGameWatch();

        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == Process.GetCurrentProcess().Id)
            {
                return;
            }

            var process = Process.GetProcessById((int)pid);
            _bigPictureLaunchedProcess = process;
            process.EnableRaisingEvents = true;

            process.Exited += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(_bigPictureLaunchedProcess, process))
                {
                    return;
                }

                StopBigPictureGameWatch();
                ViewModel_GameStopped(_bigPictureGame?.Id ?? string.Empty);
            }));

            if (process.HasExited)
            {
                StopBigPictureGameWatch();
            }
        }
        catch
        {
            // The process exited between checks: the playtime tracker still
            // reports the close a moment later.
        }
    }

    private void StopBigPictureGameWatch()
    {
        if (_bigPictureLaunchedProcess is { } process)
        {
            try
            {
                process.EnableRaisingEvents = false;
            }
            catch
            {
                // Already gone.
            }

            process.Dispose();
            _bigPictureLaunchedProcess = null;
        }

        _bigPictureGame = null;
    }
    private static bool IsInside(string folder, string path)
    {
        try
        {
            var root = Path.GetFullPath(folder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(path);

            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void StopBigPictureLaunchWatch()
    {
        _bigPictureLaunchWatch?.Stop();
        _bigPictureLaunchWatch = null;
    }

    // ------------------------------------------------------------------
// Tray (a launched game gets the whole screen; playtime keeps counting)
// ------------------------------------------------------------------

    /// <summary>
    /// Hides the window into the notification area after a game was started.
    /// The application is not closed: the playtime tracker lives in this
    /// process, so quitting would stop the counter. The tray icon brings the
    /// window back or exits for real.
    /// </summary>
    private void HideInTray()
    {
        // A running game does not need controller polling in the background.
        _gamepadWatcher.Stop();

        _tray ??= new TrayIcon(RestoreFromTray, ExitFromTray);
        _tray.RefreshTexts();
        _tray.Visible = true;
        Hide();
    }

    /// <summary>
    /// The playtime tracker reports that the game closed (or never appeared),
    /// so the window comes back on its own. A window the user already restored
    /// is left untouched.
    /// </summary>
    private async void ViewModel_GameStopped(string gameId)
    {
        StopBigPictureLaunchWatch();

        // The emulator or the game save often writes its last achievement when
        // the process exits, after the previous poll.
        if (_achievementNotifier is { } notifier)
        {
            try
            {
                await notifier.FlushLocalAsync();
            }
            catch
            {
                // A failed final read must not keep the library hidden.
            }
        }

        StopAchievementNotifications();
        _viewModel.RestoreLauncherBackground();

        // Progress, unlocked tiles and the verified badge may all have changed
        // while the launcher was hidden, so rebuild the list on return.
        _viewModel.RefreshAchievementGames();

        if (_tray is null || !_tray.Visible)
        {
            return;
        }

        RestoreFromTray();
    }

    /// <summary>Second launch of the .exe: the running window comes to the front.</summary>
    public void ShowFromExternalLaunch()
    {
        if (_bigPicture is { } bigPicture)
        {
            if (bigPicture.IsVisible)
            {
                bigPicture.Activate();
                return;
            }

            // A game is running; the tray restore brings Big Picture back.
            RestoreFromTray();
            return;
        }

        if (_tray is not null)
        {
            _tray.Visible = false;
        }

        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();

        // A window restored from the tray has no foreground rights; a short
        // topmost flip is the standard way to bring it to the user's attention.
        Topmost = true;
        Topmost = false;
    }

    private void RestoreFromTray()
    {
        if (_tray is not null)
        {
            _tray.Visible = false;
        }

        // In Big Picture the game was started from the television interface,
        // so it comes back instantly - without replaying the intro animation.
        if (_bigPicture is { } bigPicture)
        {
            if (!bigPicture.IsVisible)
            {
                bigPicture.ShowImmediate();
            }
            else
            {
                bigPicture.Activate();
            }

            return;
        }

        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        UpdateGamepadWatcher();
    }

    private void ExitFromTray()
    {
        _tray?.Dispose();
        _tray = null;
        Close();
    }

    private void OpenSteamGridDbKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(
                "https://www.steamgriddb.com/profile/preferences/api")
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // The browser may be unavailable; the key can still be pasted.
        }
    }

    private void ThemeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        var light = ThemeToggle.IsChecked == true;
        ThemeService.Apply(light ? AppTheme.Light : AppTheme.Dark);
        _store.IsLightTheme = light;
    }

    private void Language_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded ||
            (sender as FrameworkElement)?.Tag is not string tag ||
            !Enum.TryParse<AppLanguage>(tag, ignoreCase: true, out var language) ||
            Loc.Current == language)
        {
            return;
        }

        _store.Language = language;
        Loc.Apply(language);
        _tray?.RefreshTexts();
    }

    private void CpuMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded ||
            (sender as FrameworkElement)?.Tag is not string tag ||
            !Enum.TryParse<CpuOptimizationMode>(tag, ignoreCase: true, out var mode))
        {
            return;
        }

        _viewModel.CpuMode = mode;
    }

    private void Priority_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded ||
            (sender as FrameworkElement)?.Tag is not string tag ||
            !Enum.TryParse<GameProcessPriority>(tag, ignoreCase: true, out var priority))
        {
            return;
        }

        _viewModel.GamePriority = priority;
    }

    private void Timer_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded ||
            (sender as FrameworkElement)?.Tag is not string tag ||
            !Enum.TryParse<GameTimerResolution>(tag, ignoreCase: true, out var resolution))
        {
            return;
        }

        _viewModel.TimerResolution = resolution;
    }

    /// <summary>
    /// Repairs a manual entry whose folder moved to another drive. The game id
    /// stays the same, so the custom title and played time survive.
    /// </summary>
    private async void ReassignGameFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not Game game)
        {
            return;
        }

        var folder = FolderPicker.Pick(this, Loc.T("Loc.Dialog.GameFolder"), game.InstallPath);
        if (folder is null)
        {
            return;
        }

        await _viewModel.ReassignGameFolderAsync(game, folder);
    }

    // ------------------------------------------------------------------
    // Cover picker
    // ------------------------------------------------------------------

    private void ChangeCover_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is Game game)
        {
            OpenCoverPicker(game);
        }
    }

    private void CoverBadge_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if ((sender as FrameworkElement)?.DataContext is Game game)
        {
            OpenCoverPicker(game);
        }
    }

    private void OpenCoverPicker(Game game)
    {
        _coverTarget = game;
        _cropSource = null;
        CoverTitle.Text = $"{Loc.T("Loc.Cover.Title")} · {game.Title}";
        CoverSearchBox.Text = game.Title;
        CoverCropPanel.Visibility = Visibility.Collapsed;
        CoverGalleryPanel.Visibility = Visibility.Visible;
        CoverSearchRow.Visibility = Visibility.Visible;
        CoverTitle.Visibility = Visibility.Visible;
        CropPreviewImage.Source = null;
        ResetCropTransform();

        CoverOverlay.Visibility = Visibility.Visible;
        UpdateCoverSourceUi();

        if (_viewModel.IsSteamGridDbConfigured)
        {
            _ = _viewModel.SearchCoversAsync(game, game.Title);
            CoverSearchBox.Focus();
            CoverSearchBox.CaretIndex = CoverSearchBox.Text.Length;
        }
    }

    /// <summary>
    /// Steam search is gone from the picker: with SteamGridDB disabled the
    /// dialog only offers manual upload and explains how to enable it.
    /// </summary>
    private void UpdateCoverSourceUi()
    {
        var configured = _viewModel.IsSteamGridDbConfigured;

        CoverSearchField.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        CoverSearchBox.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        CoverSearchButton.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        SgdbHintPanel.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;

        if (CoverCropPanel.Visibility != Visibility.Visible)
        {
            CoverGalleryPanel.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OpenSgdbSettings_Click(object sender, RoutedEventArgs e)
    {
        CloseCoverOverlay();
        SettingsNav.IsChecked = true;
        SteamGridDbKeyBox.Focus();
    }

    private async void CoverSearch_Click(object sender, RoutedEventArgs e)
        => await SearchCoversAsync();

    private async void CoverSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SearchCoversAsync();
        }
    }

    private async Task SearchCoversAsync()
    {
        if (_coverTarget is null)
        {
            return;
        }

        await _viewModel.SearchCoversAsync(
            _coverTarget,
            CoverSearchBox.Text);
    }

    private async void CoverCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (_coverTarget is null ||
            (sender as FrameworkElement)?.Tag is not CoverCandidate candidate)
        {
            return;
        }

        if (await _viewModel.ApplyCoverAsync(_coverTarget, candidate))
        {
            CloseCoverOverlay();
        }
    }

    private void UploadCover_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("Loc.Cover.Upload"),
            Filter = Loc.T("Loc.Cover.FileFilter"),
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var image = CoverImage.LoadFile(dialog.FileName);
        if (image is null)
        {
            _viewModel.CoverStatusText = Loc.T("Loc.Cover.UploadFailed");
            return;
        }

        _cropSource = image;
        CropPreviewImage.Source = image;
        ResetCropTransform();

        CoverGalleryPanel.Visibility = Visibility.Collapsed;
        CoverCropPanel.Visibility = Visibility.Visible;
        CoverSearchRow.Visibility = Visibility.Collapsed;
        CoverTitle.Visibility = Visibility.Collapsed;
        SgdbHintPanel.Visibility = Visibility.Collapsed;
        _viewModel.CoverStatusText = string.Empty;
    }

    private void CoverCropBack_Click(object sender, RoutedEventArgs e) => ShowCoverGallery();

    private void ShowCoverGallery()
    {
        _cropSource = null;
        CropPreviewImage.Source = null;
        CoverCropPanel.Visibility = Visibility.Collapsed;
        CoverGalleryPanel.Visibility = Visibility.Visible;
        CoverSearchRow.Visibility = Visibility.Visible;
        CoverTitle.Visibility = Visibility.Visible;
        UpdateCoverSourceUi();
    }

    private void CoverCropSave_Click(object sender, RoutedEventArgs e)
    {
        if (_coverTarget is null || _cropSource is null)
        {
            return;
        }

        var width = CropViewport.ActualWidth > 0 ? CropViewport.ActualWidth : CropViewport.Width;
        var height = CropViewport.ActualHeight > 0 ? CropViewport.ActualHeight : CropViewport.Height;

        if (_viewModel.ApplyCustomCover(
                _coverTarget,
                _cropSource,
                _cropZoom,
                CropOffset.X,
                CropOffset.Y,
                width,
                height))
        {
            CloseCoverOverlay();
        }
    }

    private async void CoverReset_Click(object sender, RoutedEventArgs e)
    {
        if (_coverTarget is null)
        {
            return;
        }

        await _viewModel.ResetCoverAsync(_coverTarget);
        CloseCoverOverlay();
    }

    private void CoverOverlayCancel_Click(object sender, RoutedEventArgs e) => CloseCoverOverlay();

    private void CoverOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, CoverOverlay))
        {
            CloseCoverOverlay();
        }
    }

    private void CloseCoverOverlay()
    {
        CoverOverlay.Visibility = Visibility.Collapsed;
        CoverCropPanel.Visibility = Visibility.Collapsed;
        CoverGalleryPanel.Visibility = Visibility.Visible;
        CoverSearchRow.Visibility = Visibility.Visible;
        CoverTitle.Visibility = Visibility.Visible;
        CropPreviewImage.Source = null;
        _coverTarget = null;
        _cropSource = null;
        _cropDragging = false;
        _viewModel.CoverResults.Clear();
        _viewModel.CoverStatusText = string.Empty;
    }

    private void ResetCropTransform()
    {
        _cropZoom = 1;
        CropScale.ScaleX = 1;
        CropScale.ScaleY = 1;
        CropOffset.X = 0;
        CropOffset.Y = 0;
    }

    private void CropViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_cropSource is null)
        {
            return;
        }

        _cropDragging = true;
        _cropDragStart = e.GetPosition(CropViewport);
        _cropStartX = CropOffset.X;
        _cropStartY = CropOffset.Y;
        CropViewport.CaptureMouse();
        e.Handled = true;
    }

    private void CropViewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_cropDragging || _cropSource is null)
        {
            return;
        }

        var point = e.GetPosition(CropViewport);
        CropOffset.X = _cropStartX + (point.X - _cropDragStart.X);
        CropOffset.Y = _cropStartY + (point.Y - _cropDragStart.Y);
        ClampCropOffsets();
    }

    private void CropViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_cropDragging)
        {
            return;
        }

        _cropDragging = false;
        CropViewport.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void CropViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_cropSource is null)
        {
            return;
        }

        _cropZoom = Clamp(_cropZoom + (e.Delta > 0 ? 0.1 : -0.1), 1, 4);
        CropScale.ScaleX = _cropZoom;
        CropScale.ScaleY = _cropZoom;
        ClampCropOffsets();
        e.Handled = true;
    }

    private void ClampCropOffsets()
    {
        if (_cropSource is null)
        {
            return;
        }

        var viewportWidth = CropViewport.ActualWidth > 0
            ? CropViewport.ActualWidth
            : CropViewport.Width;
        var viewportHeight = CropViewport.ActualHeight > 0
            ? CropViewport.ActualHeight
            : CropViewport.Height;

        var baseScale = Math.Max(
            viewportWidth / _cropSource.PixelWidth,
            viewportHeight / _cropSource.PixelHeight);
        var scale = baseScale * _cropZoom;

        var maxX = Math.Max(0, (_cropSource.PixelWidth * scale - viewportWidth) / 2);
        var maxY = Math.Max(0, (_cropSource.PixelHeight * scale - viewportHeight) / 2);

        CropOffset.X = Clamp(CropOffset.X, -maxX, maxX);
        CropOffset.Y = Clamp(CropOffset.Y, -maxY, maxY);
    }

    private static double Clamp(double value, double min, double max)
        => value < min ? min : value > max ? max : value;

    // ------------------------------------------------------------------
    // Rename
    // ------------------------------------------------------------------

    private void RenameGame_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not Game game)
        {
            return;
        }

        _renameTarget = game;
        var source = Loc.SourceName(game.Source);
        RenameSubtitle.Text = string.IsNullOrWhiteSpace(game.InstallPath)
            ? source
            : $"{source} · {game.InstallPath}";
        RenameBox.Text = game.Title;

        RenameOverlay.Visibility = Visibility.Visible;
        RenameBox.Focus();
        RenameBox.CaretIndex = RenameBox.Text.Length;
    }

    private void RemoveGame_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is Game game)
        {
            _viewModel.RemoveGame(game);
        }
    }

    private void DeleteGame_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not Game game)
        {
            return;
        }

        _deleteGameTarget = game;
        _deletePlan = _viewModel.BuildUninstallPlan(game);

        DeleteGameTitle.Text = game.Title;
        DeleteTargetsText.Text = _deletePlan.Targets.Count == 0
            ? Loc.T("Loc.Delete.NoFiles")
            : string.Join(
                Environment.NewLine,
                _deletePlan.Targets.Select(target =>
                    DeleteKindLabel(target.Kind) + ": " + target.Path));

        DeleteErrorText.Text = string.Empty;
        DeleteErrorText.Visibility = Visibility.Collapsed;
        DeleteGameOverlay.Visibility = Visibility.Visible;
    }

    private void DeleteGameConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_deleteGameTarget is null || _deletePlan is null)
        {
            CloseDeleteGameOverlay();
            return;
        }

        if (_viewModel.UninstallGame(_deleteGameTarget, _deletePlan, out var error))
        {
            CloseDeleteGameOverlay();
            return;
        }

        DeleteErrorText.Text = error;
        DeleteErrorText.Visibility = Visibility.Visible;
    }

    private static string DeleteKindLabel(UninstallTargetKind kind)
        => Loc.T(kind switch
        {
            UninstallTargetKind.Install => "Loc.Delete.Kind.Install",
            UninstallTargetKind.Emulator => "Loc.Delete.Kind.Emulator",
            UninstallTargetKind.Save => "Loc.Delete.Kind.Save",
            _ => "Loc.Delete.Kind.Steam",
        });

    private void DeleteGameCancel_Click(object sender, RoutedEventArgs e)
        => CloseDeleteGameOverlay();

    private void DeleteGameOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, DeleteGameOverlay))
        {
            CloseDeleteGameOverlay();
        }
    }

    private void CloseDeleteGameOverlay()
    {
        DeleteGameOverlay.Visibility = Visibility.Collapsed;
        _deleteGameTarget = null;
        _deletePlan = null;
    }

    private void RenameSave_Click(object sender, RoutedEventArgs e) => CommitRename();

    private void RenameCancel_Click(object sender, RoutedEventArgs e) => CloseRenameOverlay();

    private void RenameOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Clicking the dimmed backdrop cancels; clicks inside the card do not.
        if (ReferenceEquals(e.OriginalSource, RenameOverlay))
        {
            CloseRenameOverlay();
        }
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitRename();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseRenameOverlay();
        }
    }

    private void CommitRename()
    {
        if (_renameTarget is null)
        {
            CloseRenameOverlay();
            return;
        }

        if (!_viewModel.RenameGame(_renameTarget, RenameBox.Text))
        {
            // Empty or unchanged name: keep the dialog open.
            RenameBox.Focus();
            return;
        }

        CloseRenameOverlay();
    }

    private void CloseRenameOverlay()
    {
        RenameOverlay.Visibility = Visibility.Collapsed;
        _renameTarget = null;
    }

    private void LaunchArguments_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not Game game)
        {
            return;
        }

        _launchArgsTarget = game;
        LaunchArgsSubtitle.Text = game.Title;
        LaunchArgsBox.Text = _viewModel.GetLaunchArguments(game);

        LaunchArgsOverlay.Visibility = Visibility.Visible;
        LaunchArgsBox.Focus();
        LaunchArgsBox.CaretIndex = LaunchArgsBox.Text.Length;
    }

    private void LaunchArgsSave_Click(object sender, RoutedEventArgs e) => CommitLaunchArgs();

    private void LaunchArgsCancel_Click(object sender, RoutedEventArgs e) => CloseLaunchArgsOverlay();

    private void LaunchArgsOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, LaunchArgsOverlay))
        {
            CloseLaunchArgsOverlay();
        }
    }

    private void LaunchArgsBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitLaunchArgs();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseLaunchArgsOverlay();
        }
    }

    private void CommitLaunchArgs()
    {
        if (_launchArgsTarget is null)
        {
            CloseLaunchArgsOverlay();
            return;
        }

        _viewModel.SetLaunchArguments(_launchArgsTarget, LaunchArgsBox.Text);
        CloseLaunchArgsOverlay();
    }

    private void CloseLaunchArgsOverlay()
    {
        LaunchArgsOverlay.Visibility = Visibility.Collapsed;
        _launchArgsTarget = null;
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (CoverOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;

                if (CoverCropPanel.Visibility == Visibility.Visible)
                {
                    ShowCoverGallery();
                }
                else
                {
                    CloseCoverOverlay();
                }
            }

            return;
        }

        if (RenameOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseRenameOverlay();
            }

            return;
        }

        if (AddGameOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseAddGameOverlay();
            }

            return;
        }

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            LibraryNav.IsChecked = true;
            SearchBox.Focus();
            SearchBox.SelectAll();
            return;
        }

        if (e.Key == Key.Escape && _viewModel.IsFiltered)
        {
            e.Handled = true;
            _viewModel.SearchQuery = string.Empty;
            return;
        }

        if (e.Key == Key.Escape && _viewModel.FavoritesOnly)
        {
            e.Handled = true;
            _viewModel.FavoritesOnly = false;
            return;
        }

        if (e.Key == Key.F11)
        {
            e.Handled = true;
            OpenBigPicture();
            return;
        }

        if (e.Key == Key.F5)
        {
            e.Handled = true;
            await _viewModel.RescanAsync();
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // This event also fires while InitializeComponent is still running.
        if (!IsLoaded || LibraryPage is null)
        {
            return;
        }

        var page = (sender as FrameworkElement)?.Tag as string;

        LibraryPage.Visibility = page == "Library" ? Visibility.Visible : Visibility.Collapsed;
        AchievementsPage.Visibility = page == "Achievements" ? Visibility.Visible : Visibility.Collapsed;
        ScanPage.Visibility = page == "Scan" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;

        switch (page)
        {
            case "Library":
                FadeIn(LibraryPage);
                break;
            case "Achievements":
                _viewModel.RefreshAchievementGames();
                FadeIn(AchievementsPage);
                break;
            case "Scan":
                FadeIn(ScanPage);
                break;
            case "Settings":
                FadeIn(SettingsPage);
                break;
        }
    }

    private void ViewMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || GamesItems is null)
        {
            return;
        }

        var isGrid = ReferenceEquals(sender, GridViewButton);
        ApplyViewMode(isGrid);
        _store.GridView = isGrid;

        ScrollHost.ScrollToTop();
    }

    private void ApplyViewMode(bool isGrid)
    {
        GamesItems.ItemsPanel = (ItemsPanelTemplate)FindResource(
            isGrid ? "GamesGridPanel" : "GamesListPanel");
        GamesItems.ItemTemplate = (DataTemplate)FindResource(
            isGrid ? "GameCardTemplate" : "GameRowTemplate");
    }

    private async void GroupMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingGrouping || !IsLoaded || GamesItems is null)
        {
            return;
        }

        if ((sender as FrameworkElement)?.Tag is not string tag ||
            !Enum.TryParse<LibraryGrouping>(tag, out var grouping))
        {
            return;
        }

        await _viewModel.SetGroupingAsync(grouping);
        ScrollHost.ScrollToTop();
    }

    private void SelectGrouping(LibraryGrouping grouping)
    {
        _syncingGrouping = true;
        try
        {
            switch (grouping)
            {
                case LibraryGrouping.Alphabet:
                    GroupAlphaButton.IsChecked = true;
                    break;
                case LibraryGrouping.Genre:
                    GroupGenreButton.IsChecked = true;
                    break;
                case LibraryGrouping.Series:
                    GroupSeriesButton.IsChecked = true;
                    break;
                default:
                    GroupAllButton.IsChecked = true;
                    break;
            }
        }
        finally
        {
            _syncingGrouping = false;
        }
    }

    private void OpenCacheFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = _viewModel.CoverCacheDirectory;
            Directory.CreateDirectory(directory);

            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Explorer is unavailable in rare environments; nothing critical.
        }
    }

    private async void ChangeCacheFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = FolderPicker.Pick(
            this,
            Loc.T("Loc.Dialog.CacheFolder"),
            _viewModel.CoverCacheDirectory);

        if (folder is null)
        {
            return;
        }

        await _viewModel.ChangeCoverCacheDirectoryAsync(folder);
    }

    private async void ResetCacheFolder_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ChangeCoverCacheDirectoryAsync(null);
    }

    private static void FadeIn(UIElement element)
    {
        element.Opacity = 1;
        element.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170))
            {
                FillBehavior = FillBehavior.Stop,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    protected override void OnClosed(EventArgs e)
    {
        StopBigPictureLaunchWatch();
        StopGamePlacementWatch();
        StopAchievementNotifications();
        _achievementNotifier?.Dispose();
        _achievementNotifier = null;

        _bigPicture?.CloseApproved();
        _bigPicture = null;

        _tray?.Dispose();
        _tray = null;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.GameStopped -= ViewModel_GameStopped;
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
