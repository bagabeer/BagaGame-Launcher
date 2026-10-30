using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BagaGame.Models;
using BagaGame.Services;
using BagaGame.ViewModels;
using Microsoft.Win32;

namespace BagaGame;

public partial class MainWindow : BagaGame.Controls.BagaWindow
{
    private readonly MainViewModel _viewModel;
    private readonly LibraryStore _store;
    private Game? _renameTarget;
    private TrayIcon? _tray;

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

        _viewModel = new MainViewModel(
            new GameLibraryService(store.CoverCacheDirectory),
            store);
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.GameStopped += ViewModel_GameStopped;
        DataContext = _viewModel;

        // Setting IsChecked before Loaded does not trigger a theme swap; the
        // startup theme was already applied by App.OnStartup.
        ThemeToggle.IsChecked = store.IsLightTheme;

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

        // Covers stream in after the window is already visible.
        await _viewModel.InitializeAsync();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsScanning))
        {
            UpdateScanAnimation();
        }
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

    // ------------------------------------------------------------------
    // Launch / context menu
    // ------------------------------------------------------------------

    private void GameCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Game game &&
            _viewModel.LaunchGame(game))
        {
            HideInTray(game.Title);
        }
    }

    private void LaunchGame_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is Game game &&
            _viewModel.LaunchGame(game))
        {
            HideInTray(game.Title);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is Game game)
        {
            _viewModel.OpenGameFolder(game);
        }
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
    private void HideInTray(string gameTitle)
    {
        _tray ??= new TrayIcon(RestoreFromTray, ExitFromTray);
        _tray.RefreshTexts();
        _tray.Visible = true;
        _tray.ShowBalloon("BagaGame", Loc.Format("Loc.Tray.Launched", gameTitle));
        Hide();
    }

    /// <summary>
    /// The playtime tracker reports that the game closed (or never appeared),
    /// so the window comes back on its own. A window the user already restored
    /// is left untouched.
    /// </summary>
    private void ViewModel_GameStopped(string gameId)
    {
        if (_tray is null || !_tray.Visible)
        {
            return;
        }

        RestoreFromTray();
    }

    /// <summary>Second launch of the .exe: the running window comes to the front.</summary>
    public void ShowFromExternalLaunch()
    {
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

        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void ExitFromTray()
    {
        _tray?.Dispose();
        _tray = null;
        Close();
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
        RenameBox.SelectAll();
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
        if ((sender as MenuItem)?.DataContext is Game game)
        {
            _viewModel.DeleteGame(game);
        }
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

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
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
        ScanPage.Visibility = page == "Scan" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;

        switch (page)
        {
            case "Library":
                FadeIn(LibraryPage);
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
        if (!IsLoaded || GamesItems is null)
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
        _tray?.Dispose();
        _tray = null;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.GameStopped -= ViewModel_GameStopped;
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
