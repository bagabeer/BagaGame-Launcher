using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace BagaGame.Controls;

/// <summary>
/// A single-line label that scrolls until the whole text has been shown, then
/// starts again from the beginning. Short labels stay still.
/// </summary>
public sealed class MarqueeTextBlock : UserControl
{
    private const double PixelsPerSecond = 22;

    private readonly TextBlock _textBlock;
    private readonly TranslateTransform _offset = new();

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(MarqueeTextBlock),
        new PropertyMetadata(string.Empty, OnTextChanged));

    public MarqueeTextBlock()
    {
        ClipToBounds = true;
        HorizontalContentAlignment = HorizontalAlignment.Left;
        VerticalContentAlignment = VerticalAlignment.Center;

        _textBlock = new TextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.None,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _offset,
        };

        // The scroll viewer measures the label with an infinite width, so the
        // whole title is painted and only the visible viewport is clipped.
        var viewport = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Focusable = false,
            IsTabStop = false,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = _textBlock,
        };

        Content = viewport;

        Loaded += (_, _) => Restart();
        Unloaded += (_, _) => Stop();
        SizeChanged += (_, _) => Restart();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var control = (MarqueeTextBlock)sender;
        control._textBlock.Text = e.NewValue as string ?? string.Empty;
        control.Dispatcher.BeginInvoke(new Action(control.Restart), DispatcherPriority.Loaded);
    }

    private void Restart()
    {
        if (!IsLoaded || ActualWidth <= 0)
        {
            Stop();
            return;
        }

        _offset.BeginAnimation(TranslateTransform.XProperty, null);
        _offset.X = 0;

        var text = Text ?? string.Empty;
        _textBlock.Text = text;
        _textBlock.Width = double.NaN;

        var textWidth = MeasureText(text);
        var overflow = textWidth - ActualWidth + 12;

        if (overflow <= 13)
        {
            _textBlock.Width = textWidth > 0 ? textWidth : double.NaN;
            return;
        }

        // The viewport clips the label, but the label itself must be wide
        // enough to paint the hidden tail before the transform moves it in.
        _textBlock.Width = textWidth;

        var travel = TimeSpan.FromSeconds(Math.Max(2.0, overflow / PixelsPerSecond));
        var pause = TimeSpan.FromMilliseconds(800);
        var total = pause + travel + pause + TimeSpan.FromMilliseconds(40);

        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = total,
            RepeatBehavior = RepeatBehavior.Forever,
        };

        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(pause)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(pause + travel)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(pause + travel + pause)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(total)));

        _offset.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private double MeasureText(string value)
    {
        try
        {
            var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
            var text = new FormattedText(
                value,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                FontSize,
                Foreground ?? Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            return text.WidthIncludingTrailingWhitespace;
        }
        catch
        {
            return 0;
        }
    }

    private void Stop()
    {
        _offset.BeginAnimation(TranslateTransform.XProperty, null);
        _offset.X = 0;
        _textBlock.Text = Text ?? string.Empty;
        _textBlock.Width = double.NaN;
    }
}
