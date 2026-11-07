using System.Windows;
using System.Windows.Media;

namespace BagaGame.Controls;

/// <summary>
/// Clips a scroll area to rounded corners. A Border does not clip its child to
/// its CornerRadius, so the ScrollViewer itself gets a rounded geometry clip.
/// Attached to the shared OverlayScrollViewer style, so every list in the
/// launcher gets the same treatment.
/// </summary>
public static class RoundedClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius",
        typeof(double),
        typeof(RoundedClip),
        new PropertyMetadata(0.0, OnRadiusChanged));

    public static void SetRadius(DependencyObject element, double value)
        => element.SetValue(RadiusProperty, value);

    public static double GetRadius(DependencyObject element)
        => (double)element.GetValue(RadiusProperty);

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.SizeChanged -= OnSizeChanged;
        element.SizeChanged += OnSizeChanged;
        Apply(element);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
        => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement element)
    {
        var radius = GetRadius(element);
        if (radius <= 0 || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            element.Clip = null;
            return;
        }

        element.Clip = new RectangleGeometry(
            new Rect(0, 0, element.ActualWidth, element.ActualHeight),
            radius,
            radius);
    }
}
