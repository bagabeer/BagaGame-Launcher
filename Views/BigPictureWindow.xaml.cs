using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private readonly Dictionary<string, GameMetadata?> _metadataCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageSource?> _heroCache = new(StringComparer.Ordinal);
    private CancellationTokenSource? _detailsToken;
    private DispatcherTimer? _introTimer;
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

        GamesList.PreviewMouseWheel += OnGamesListMouseWheel;
        GamesList.MouseDoubleClick += (_, _) => PlaySelected();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Big Picture owns the pad; the main window never sees these events.
        _gamepad.Command += OnPadCommand;
        _gamepad.ConnectedChanged += connected => Dispatcher.BeginInvoke(
            new Action(() => UpdateHints(connected)));
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

        GamesList.Focus();
    }

    /// <summary>Fades the veil in; the caller swaps the windows when it completes.</summary>
    public void PlayExit(Action completed)
    {
        _gamepad.Stop();
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
        GamesList.Focus();
        _ready = true;
        _gamepad.Start();
        UpdateHints(_gamepad.IsConnected);
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
            bitmap.DecodePixelWidth = 1600;
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

    private void OnPadCommand(PadCommand command)
    {
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
                        GamesList.Focus();
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

    private void UpdateHints(bool padConnected)
    {
        KeyboardHints.Visibility = padConnected ? Visibility.Collapsed : Visibility.Visible;
        PadHints.Visibility = padConnected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SelectCurrent()
    {
        if (_viewModel.SelectedGame is { } game)
        {
            GamesList.SelectedItem = game;
            ScrollToSelection();
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
        if (GamesList.SelectedItem is not Game game)
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
            GamesList.Focus();
        }
    }

    private void CloseDetails_Click(object sender, RoutedEventArgs e)
    {
        DetailsOverlay.Visibility = Visibility.Collapsed;
        GamesList.Focus();
    }

    private void DetailsOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, DetailsOverlay))
        {
            DetailsOverlay.Visibility = Visibility.Collapsed;
            GamesList.Focus();
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke("Settings");

    /// <summary>Short brand animation over the entrance veil.</summary>
    private void PlayIntro()
    {
        IntroLogo.Visibility = Visibility.Visible;
        IntroLogo.Opacity = 0;
        IntroLogoScale.ScaleX = 0.9;
        IntroLogoScale.ScaleY = 0.9;

        var zoom = new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(380))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        IntroLogoScale.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
        IntroLogoScale.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);

        IntroLogo.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));

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
            case Key.Escape:
                e.Handled = true;

                if (DetailsOverlay.Visibility == Visibility.Visible)
                {
                    DetailsOverlay.Visibility = Visibility.Collapsed;
                    GamesList.Focus();
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

    protected override void OnClosed(EventArgs e)
    {
        _gamepad.Dispose();

        _detailsToken?.Cancel();
        _detailsToken?.Dispose();
        _detailsToken = null;

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        base.OnClosed(e);
    }
}
