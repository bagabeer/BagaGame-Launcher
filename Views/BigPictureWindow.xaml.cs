using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BagaGame.Models;
using BagaGame.Services;
using BagaGame.Services.Audio;
using BagaGame.Services.Input;
using BagaGame.ViewModels;

namespace BagaGame.Views;

/// <summary>
/// Fullscreen television interface. It shares the library with the main window
/// and adds only presentation: large artwork, one horizontal row of covers and
/// keyboard/pad driven navigation. The veil covers the window during the switch
/// from the library so the mode change reads as a fade, not as a window popup.
/// </summary>
public partial class BigPictureWindow : Window
{
    private readonly GameLibraryService _library;
    private readonly LibraryStore _store;
    private readonly BigPictureViewModel _viewModel;
    private readonly GamepadService _gamepad = new();
    private readonly DispatcherTimer _batteryTimer;
    private readonly Dictionary<string, GameMetadata?> _metadataCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageSource?> _heroCache = new(StringComparer.Ordinal);
    private CancellationTokenSource? _detailsToken;
    private DispatcherTimer? _introTimer;
    private bool _cursorHidden;
    private bool _closeApproved;
    private bool _ready;
    private Game? _lastTickGame;

    public BigPictureWindow(MainViewModel main, LibraryStore store)
    {
        InitializeComponent();

        _store = store;
        _library = main.LibraryService;
        _viewModel = new BigPictureViewModel(main, store);
        DataContext = _viewModel;

        Root.RenderTransformOrigin = new Point(0.5, 0.5);
        Root.RenderTransform = new ScaleTransform(1, 1);

        SizeChanged += (_, _) => ApplyScale();
        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
        IsVisibleChanged += OnVisibleChanged;

        MouseMove += (_, _) => ShowCursor();

        GamesList.PreviewMouseWheel += OnGamesListMouseWheel;
        GamesList.MouseDoubleClick += (_, _) => PlaySelected();
        GamesGrid.MouseDoubleClick += (_, _) => PlaySelected();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Big Picture owns the pad; the main window never sees these events.
        _gamepad.Command += OnPadCommand;
        _gamepad.ConnectedChanged += connected => Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateHints(connected);
            UpdateBattery();
        }));

        _batteryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _batteryTimer.Tick += (_, _) => UpdateBattery();
    }

    /// <summary>The user pressed Esc, F11 or closed the window: leave the mode.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>"Сканирование" and "Настройки" open the same page in the library window.</summary>
    public event Action<string>? NavigateRequested;

    /// <summary>The main window starts the game and handles the tray.</summary>
    public event Action<Game>? PlayRequested;

    public BigPictureViewModel ViewModel => _viewModel;

    /// <summary>Called right before the window is shown or restored from the tray.</summary>
    public void PrepareForShow()
    {
        SnapToOwnerMonitor();
        ApplyScale();
        Veil.Opacity = 1;
        _viewModel.RefreshOrder();
        _viewModel.EnsureSelection();
        SelectCurrent();
        ScheduleDetailsLoad();
    }

    /// <summary>Fades the veil away and settles the layout with a small zoom-out.</summary>
    public void PlayEntrance()
    {
        FillMonitorPhysical();
        PlayIntro();
        Veil.Opacity = 1;

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(340))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        fade.Completed += (_, _) => Veil.Opacity = 0;
        Veil.BeginAnimation(OpacityProperty, fade);

        if (Root.RenderTransform is ScaleTransform scale)
        {
            var zoom = new DoubleAnimation(1.012, 1, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            };

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
        }

        FocusGames();
    }

    /// <summary>
    /// Shows the window instantly after a game closes: no veil, no intro logo,
    /// no re-selection animation. The player gets the same card back at once.
    /// </summary>
    public void ShowImmediate()
    {
        _introTimer?.Stop();
        _introTimer = null;
        IntroLogo.Visibility = Visibility.Collapsed;
        IntroLogo.Opacity = 0;
        Veil.Opacity = 0;

        SnapToOwnerMonitor();
        ApplyScale();
        _viewModel.RefreshOrder();
        _viewModel.EnsureSelection();
        SelectCurrent();

        Show();
        Activate();
        FillMonitorPhysical();

        Topmost = true;
        Topmost = false;

        _ready = true;
        FocusGames();
        _gamepad.Start();
        UpdateHints(_gamepad.IsConnected);
        UpdateBattery();
        _batteryTimer.Start();
        ScheduleDetailsLoad();
    }

    /// <summary>Fades the veil in; the caller swaps the windows when it completes.</summary>
    public void PlayExit(Action completed)
    {
        ShowCursor();
        _gamepad.Stop();
        _batteryTimer.Stop();
        _detailsToken?.Cancel();
        _introTimer?.Stop();
        _introTimer = null;
        IntroLogo.Visibility = Visibility.Collapsed;
        Veil.Opacity = 0;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop,
        };

        fade.Completed += (_, _) => completed();
        Veil.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Closes the window without asking the owner to animate the exit.</summary>
    public void CloseApproved()
    {
        _closeApproved = true;
        Close();
    }

    /// <summary>
    /// Pins the window to the monitor that owns the library window and fills
    /// the whole screen, taskbar included. Windows would otherwise maximize on
    /// the monitor under the cursor, which can be a different display.
    /// </summary>
    private void SnapToOwnerMonitor()
    {
        try
        {
            if (Owner is not { } owner)
            {
                return;
            }

            var screen = System.Windows.Forms.Screen.FromHandle(
                new WindowInteropHelper(owner).Handle);
            var source = PresentationSource.FromVisual(owner);
            var toDevice = source?.CompositionTarget?.TransformFromDevice;
            if (toDevice is null)
            {
                return;
            }

            var topLeft = toDevice.Value.Transform(
                new Point(screen.Bounds.Left, screen.Bounds.Top));
            var bottomRight = toDevice.Value.Transform(
                new Point(screen.Bounds.Right, screen.Bounds.Bottom));

            WindowState = WindowState.Normal;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = topLeft.X;
            Top = topLeft.Y;
            Width = Math.Max(320, bottomRight.X - topLeft.X);
            Height = Math.Max(240, bottomRight.Y - topLeft.Y);
        }
        catch
        {
            // Keep the default placement if the monitor query fails.
        }
    }

    /// <summary>
    /// Pins the window to the monitor's physical bounds. DIP math can leave a
    /// one-pixel desktop stripe on displays with a different DPI.
    /// </summary>
    private void FillMonitorPhysical()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var bounds = System.Windows.Forms.Screen.FromHandle(handle).Bounds;
            SetWindowPos(
                handle,
                IntPtr.Zero,
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                0x0004 | 0x0010);
        }
        catch
        {
            // Keep the managed placement if the native call fails.
        }
    }

    private void ApplyScale()
    {
        var scale = UiScale.ForHeight(ActualHeight);
        RootScale.ScaleX = scale;
        RootScale.ScaleY = scale;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyScale();
        _viewModel.EnsureSelection();
        SelectCurrent();
        FocusGames();
        _ready = true;
        _gamepad.Start();
        UpdateHints(_gamepad.IsConnected);
        UpdateBattery();
        _batteryTimer.Start();
        ScheduleDetailsLoad();
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Polling stops while a launched game owns the screen.
        if (IsVisible)
        {
            _gamepad.Start();
        }
        else
        {
            _gamepad.Stop();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BigPictureViewModel.SelectedGame))
        {
            ScheduleDetailsLoad();
        }
    }

    /// <summary>
    /// Loads the wide artwork and the store text for the selected card. A quick
    /// scroll cancels the previous request, so only the last card is fetched.
    /// </summary>
    private void ScheduleDetailsLoad()
    {
        _detailsToken?.Cancel();
        _detailsToken?.Dispose();

        var token = new CancellationTokenSource();
        _detailsToken = token;

        var game = _viewModel.SelectedGame;

        if (game is null)
        {
            _viewModel.SetHero(null);
            _viewModel.SetMetadata(null, null);
            return;
        }

        // Cached cards appear instantly and do not replay the fade-in.
        var hasMetadata = _metadataCache.TryGetValue(game.Id, out var cachedMetadata);
        var hasHero = _heroCache.TryGetValue(game.Id, out var cachedHero);

        if (hasMetadata)
        {
            _viewModel.SetMetadata(cachedMetadata?.Description, cachedMetadata?.Genres);
        }

        if (hasHero)
        {
            _viewModel.SetHero(cachedHero);
        }

        if (hasMetadata && hasHero && cachedHero is not null)
        {
            return;
        }

        if (!hasHero)
        {
            _viewModel.SetHero(null);
        }

        if (!hasMetadata)
        {
            _viewModel.SetMetadata(null, null);
        }

        _ = LoadDetailsAsync(game, token.Token);
    }

    private async Task LoadDetailsAsync(Game game, CancellationToken cancellationToken)
    {
        try
        {
            // Small debounce: holding an arrow key must not spam the store API.
            await Task.Delay(160, cancellationToken);

            if (!_metadataCache.ContainsKey(game.Id))
            {
                var metadata = await _library.GetGameMetadataAsync(game, cancellationToken);
                _metadataCache[game.Id] = metadata;

                if (!cancellationToken.IsCancellationRequested &&
                    ReferenceEquals(_viewModel.SelectedGame, game))
                {
                    _viewModel.SetMetadata(metadata?.Description, metadata?.Genres);
                }
            }

            if (_heroCache.ContainsKey(game.Id) && _heroCache[game.Id] is not null)
            {
                return;
            }

            var heroPath = await _library.GetHeroArtworkAsync(
                game,
                _store.SteamGridDbApiKey,
                cancellationToken);

            var hero = LoadBitmap(heroPath);
            _heroCache[game.Id] = hero;

            if (!cancellationToken.IsCancellationRequested &&
                ReferenceEquals(_viewModel.SelectedGame, game))
            {
                _viewModel.SetHero(hero);
            }
        }
        catch (OperationCanceledException)
        {
            // The selection changed; the newer request owns the banner.
        }
        catch
        {
            // Offline or a store hiccup: the poster fallback stays.
        }
    }

    private static ImageSource? LoadBitmap(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.DecodePixelWidth = HeroDecodeWidth;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Wide artwork is sharpest when decoded at the panel's real width.</summary>
    private static int HeroDecodeWidth
    {
        get
        {
            try
            {
                var width = System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Width ?? 1920;
                return width >= 3000 ? 2560 : 1920;
            }
            catch
            {
                return 1920;
            }
        }
    }

    private void OnPadCommand(PadCommand command)
    {
        // Any controller input is a signal to get the pointer out of the way.
        HideCursor();

        try
        {
            switch (command)
            {
                case PadCommand.Left:
                    _viewModel.Move(-1);
                    break;

                case PadCommand.Right:
                    _viewModel.Move(1);
                    break;

                case PadCommand.ToggleGrid:
                    ToggleGridView();
                    break;

                case PadCommand.Down:
                    if (_viewModel.IsGridMode)
                    {
                        MoveGridRows(1);
                    }

                    break;

                case PadCommand.Up:
                    if (_viewModel.IsGridMode)
                    {
                        MoveGridRows(-1);
                    }

                    break;

                case PadCommand.Play:
                    PlaySelected();
                    break;

                case PadCommand.Details:
                    ToggleDetails();
                    break;

                case PadCommand.Menu:
                    NavigateRequested?.Invoke("Settings");
                    break;

                case PadCommand.Back:
                    if (DetailsOverlay.Visibility == Visibility.Visible)
                    {
                        DetailsOverlay.Visibility = Visibility.Collapsed;
                        FocusGames();
                    }
                    else
                    {
                        ExitRequested?.Invoke(this, EventArgs.Empty);
                    }

                    break;
            }
        }
        catch
        {
            // A pad event must never take the window down.
        }
    }

    private void UpdateBattery()
    {
        var connected = _gamepad.IsConnected;
        _viewModel.SetBattery(
            connected ? _gamepad.BatteryPercent : -1,
            connected && _gamepad.BatteryWired);
    }

    private void UpdateHints(bool padConnected)
    {
        KeyboardHints.Visibility = padConnected ? Visibility.Collapsed : Visibility.Visible;
        PadHints.Visibility = padConnected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SelectCurrent()
    {
        if (_viewModel.SelectedGame is not { } game)
        {
            return;
        }

        if (_viewModel.IsGridMode)
        {
            GamesGrid.SelectedItem = game;
            GamesGrid.ScrollIntoView(game);
        }
        else
        {
            GamesList.SelectedItem = game;
            ScrollToSelection();
        }
    }

    private void FocusGames()
    {
        if (_viewModel.IsGridMode)
        {
            GamesGrid.Focus();
        }
        else
        {
            GamesList.Focus();
        }
    }

    /// <summary>One button (D-pad Down or Down) flips between both views.</summary>
    private void ToggleGridView()
    {
        if (DetailsOverlay.Visibility == Visibility.Visible)
        {
            return;
        }

        if (_viewModel.IsGridMode)
        {
            ExitGrid();
        }
        else
        {
            EnterGrid();
        }
    }

    /// <summary>Carousel to compact grid; the selection stays on the same game.</summary>
    private void EnterGrid()
    {
        if (DetailsOverlay.Visibility == Visibility.Visible || _viewModel.IsGridMode)
        {
            return;
        }

        _viewModel.IsGridMode = true;

        // The list is laid out only after the trigger made it visible.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            GamesGrid.SelectedItem = _viewModel.SelectedGame;
            GamesGrid.Focus();
            if (_viewModel.SelectedGame is { } game)
            {
                GamesGrid.ScrollIntoView(game);
            }
        }), DispatcherPriority.Loaded);
    }

    /// <summary>Grid back to the carousel, keeping the selected game.</summary>
    private void ExitGrid()
    {
        if (!_viewModel.IsGridMode)
        {
            return;
        }

        _viewModel.IsGridMode = false;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            GamesList.SelectedItem = _viewModel.SelectedGame;
            FocusGames();
            ScrollToSelection();
        }), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Moves one row inside the grid. The next card is picked by its actual
    /// position, so a half-filled last row and any number of columns still
    /// land on the nearest cover below or above. Edges simply stop.
    /// </summary>
    private void MoveGridRows(int direction)
    {
        if (_viewModel.SelectedGame is not { } current ||
            GamesGrid.ItemContainerGenerator.ContainerFromItem(current) is not ListBoxItem currentItem)
        {
            return;
        }

        var origin = currentItem.TranslatePoint(new Point(0, 0), GamesGrid);
        var centerX = origin.X + currentItem.ActualWidth / 2;

        Game? best = null;
        var bestRowDistance = double.MaxValue;
        var bestColumnDistance = double.MaxValue;

        foreach (var game in _viewModel.Games)
        {
            if (ReferenceEquals(game, current) ||
                GamesGrid.ItemContainerGenerator.ContainerFromItem(game) is not ListBoxItem item)
            {
                continue;
            }

            var position = item.TranslatePoint(new Point(0, 0), GamesGrid);
            var rowDistance = position.Y - origin.Y;

            if (direction > 0 ? rowDistance <= 1 : rowDistance >= -1)
            {
                continue;
            }

            rowDistance = Math.Abs(rowDistance);
            var columnDistance = Math.Abs(position.X + item.ActualWidth / 2 - centerX);

            if (rowDistance < bestRowDistance - 0.5 ||
                (Math.Abs(rowDistance - bestRowDistance) <= 0.5 &&
                 columnDistance < bestColumnDistance))
            {
                best = game;
                bestRowDistance = rowDistance;
                bestColumnDistance = columnDistance;
            }
        }

        if (best is not null)
        {
            _viewModel.SelectedGame = best;
            GamesGrid.ScrollIntoView(best);
        }
    }

    private void GamesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_viewModel.IsGridMode)
        {
            return;
        }

        if (_viewModel.SelectedGame is { } game)
        {
            GamesGrid.ScrollIntoView(game);
        }

        if (_ready && GamesGrid.SelectedItem is Game selected &&
            !ReferenceEquals(_lastTickGame, selected))
        {
            UiSounds.PlayMove(_store.BigPictureSounds);
            _lastTickGame = selected;
        }
    }

    private void ScrollToSelection()
    {
        if (GamesList.SelectedItem is { } item &&
            GamesList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
        {
            container.BringIntoView();
        }
    }

    private void GamesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // While the compact grid is shown it owns the focus, sound and scroll.
        if (_viewModel.IsGridMode || GamesList.SelectedItem is not Game game)
        {
            return;
        }

        ScrollToSelection();

        if (_ready && !ReferenceEquals(_lastTickGame, game))
        {
            UiSounds.PlayMove(_store.BigPictureSounds);
        }

        _lastTickGame = game;
    }

    private void OnGamesListMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0)
        {
            return;
        }

        _viewModel.Move(e.Delta > 0 ? -1 : 1);
        e.Handled = true;
    }

    private void Play_Click(object sender, RoutedEventArgs e) => PlaySelected();

    private void HideCursor()
    {
        if (_cursorHidden)
        {
            return;
        }

        _cursorHidden = true;
        Mouse.OverrideCursor = Cursors.None;
    }

    private void ShowCursor()
    {
        if (!_cursorHidden)
        {
            return;
        }

        _cursorHidden = false;
        Mouse.OverrideCursor = null;
    }

    private void PlaySelected()
    {
        if (_viewModel.SelectedGame is { } game)
        {
            PlayRequested?.Invoke(game);
        }
    }

    private void Details_Click(object sender, RoutedEventArgs e) => ToggleDetails();

    private void ToggleDetails()
    {
        DetailsOverlay.Visibility = DetailsOverlay.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (DetailsOverlay.Visibility == Visibility.Visible)
        {
            DetailsOverlay.Focus();
        }
        else
        {
            FocusGames();
        }
    }

    private void CloseDetails_Click(object sender, RoutedEventArgs e)
    {
        DetailsOverlay.Visibility = Visibility.Collapsed;
        FocusGames();
    }

    private void DetailsOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, DetailsOverlay))
        {
            DetailsOverlay.Visibility = Visibility.Collapsed;
            FocusGames();
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke("Settings");

    /// <summary>Short brand animation over the entrance veil.</summary>
    private void PlayIntro()
    {
        IntroLogo.Visibility = Visibility.Visible;

        // The overlay is opaque from the first frame: the library behind it
        // must never be visible before the logo animation ends.
        IntroLogo.Opacity = 1;
        IntroLogoScale.ScaleX = 0.9;
        IntroLogoScale.ScaleY = 0.9;

        var zoom = new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(380))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        IntroLogoScale.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
        IntroLogoScale.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);

        _introTimer?.Stop();
        _introTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        _introTimer.Tick += (_, _) =>
        {
            _introTimer?.Stop();
            _introTimer = null;

            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };

            fadeOut.Completed += (_, _) =>
            {
                IntroLogo.Visibility = Visibility.Collapsed;
                IntroLogo.Opacity = 0;
            };

            IntroLogo.BeginAnimation(OpacityProperty, fadeOut);
        };

        _introTimer.Start();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
                e.Handled = true;
                _viewModel.Move(-1);
                return;

            case Key.Right:
                e.Handled = true;
                _viewModel.Move(1);
                return;

            case Key.Down:
                e.Handled = true;
                if (_viewModel.IsGridMode)
                {
                    MoveGridRows(1);
                }
                else
                {
                    EnterGrid();
                }

                return;

            case Key.Up:
                if (_viewModel.IsGridMode)
                {
                    e.Handled = true;
                    MoveGridRows(-1);
                }

                return;

            case Key.Escape:
                e.Handled = true;

                if (DetailsOverlay.Visibility == Visibility.Visible)
                {
                    DetailsOverlay.Visibility = Visibility.Collapsed;
                    FocusGames();
                }
                else if (_viewModel.IsGridMode)
                {
                    ExitGrid();
                }
                else
                {
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                }

                return;

            case Key.F11:
                e.Handled = true;
                ExitRequested?.Invoke(this, EventArgs.Empty);
                return;

            case Key.Enter:
                e.Handled = true;
                PlaySelected();
                return;

            case Key.Space:
                e.Handled = true;
                ToggleDetails();
                return;
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_closeApproved)
        {
            return;
        }

        // Alt+F4 asks the owner for the animated exit instead of closing instantly.
        e.Cancel = true;
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    protected override void OnClosed(EventArgs e)
    {
        ShowCursor();
        _gamepad.Dispose();

        _detailsToken?.Cancel();
        _detailsToken?.Dispose();
        _detailsToken = null;

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        base.OnClosed(e);
    }
}
