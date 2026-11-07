using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BagaGame.Services;

namespace BagaGame.Views;

/// <summary>
/// Small achievement toast with the unlocked artwork. It is a notification,
/// not an in-game overlay: it appears above the desktop for a few seconds and
/// never takes focus.
/// </summary>
public partial class AchievementToastWindow : Window
{
    private readonly DispatcherTimer _closeTimer;
    private readonly bool _compact;

    public AchievementToastWindow(string title, string text, string? iconPath, bool compact = false)
    {
        InitializeComponent();

        _compact = compact;

        TitleText.Text = title;
        BodyText.Text = text;

        if (compact)
        {
            Width = 320;
            Height = 66;
            Card.Margin = new Thickness(8);
            Card.Padding = new Thickness(10);
            IconFrame.Visibility = Visibility.Collapsed;

            if (IconFrame.Parent is System.Windows.Controls.Grid grid &&
                grid.ColumnDefinitions.Count > 1)
            {
                grid.ColumnDefinitions[1].Width = new GridLength(0);
            }

            TitleText.FontSize = 10;
            BodyText.FontSize = 12.5;
            BodyText.Margin = new Thickness(0, 2, 0, 0);
        }

        // The toast is its own transparent window, so a failed theme lookup
        // would leave it with no card at all. Keep the dark fallback from XAML
        // and upgrade to the theme gradient when it is available.
        if (TryFindResource("ToastBackgroundBrush") is Brush background)
        {
            Card.Background = background;
        }
        else if (TryFindResource("WindowTintBrush") is Brush tint)
        {
            Card.Background = tint;
        }

        if (TryFindResource("AccentBrush") is Brush accent)
        {
            AccentStrip.Background = accent;
        }

        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(iconPath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                IconImage.Source = bitmap;
            }
            catch
            {
                IconFrame.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            IconFrame.Visibility = Visibility.Collapsed;
        }

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            FadeOut();
        };

        Loaded += OnLoaded;
        Closed += (_, _) => _closeTimer.Stop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 8;
        Top = area.Bottom - Height - 8;

        if (!_compact)
        {
            AchievementSound.Play();
        }

        BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));

        _closeTimer.Start();
    }

    private void FadeOut()
    {
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Close();
}
