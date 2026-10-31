using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace BagaGame.Views;

/// <summary>
/// Brand splash shown while the library is being prepared. It never takes
/// focus, so the main window can still appear behind it and the splash simply
/// fades away after a short pause.
/// </summary>
public partial class SplashWindow : Window
{
    private DispatcherTimer? _timer;

    public SplashWindow()
    {
        InitializeComponent();

        // Same height-based scale as Big Picture, so the splash is not tiny on
        // a 1440p or 4K television.
        var scale = Services.UiScale.ForHeight(SystemParameters.PrimaryScreenHeight);
        Width *= scale;
        Height *= scale;

        // Center on the primary screen, like the library window. CenterScreen
        // would follow the mouse and could place the splash on another monitor.
        var area = SystemParameters.WorkArea;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;

        if (Content is FrameworkElement root)
        {
            root.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
        }

        Loaded += (_, _) => PlayEntrance();
    }

    /// <summary>Fades the splash out after the given delay and closes it.</summary>
    public void CloseAfter(TimeSpan delay)
    {
        _timer = new DispatcherTimer { Interval = delay };
        _timer.Tick += (_, _) =>
        {
            _timer?.Stop();
            _timer = null;
            FadeOut();
        };
        _timer.Start();
    }

    private void PlayEntrance()
    {
        LogoScale.ScaleX = 0.9;
        LogoScale.ScaleY = 0.9;

        var zoom = new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, zoom);
        LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, zoom);

        var slide = new DoubleAnimation(42, 112, TimeSpan.FromMilliseconds(900))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            BeginTime = TimeSpan.FromMilliseconds(120),
        };

        Progress.BeginAnimation(WidthProperty, slide);
    }

    private void FadeOut()
    {
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };

        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
