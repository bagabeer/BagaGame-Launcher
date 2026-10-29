using System.Windows;
using System.Windows.Controls;

namespace BagaGame.Controls;

public enum ButtonAppearance
{
    Secondary,
    Primary,
}

/// <summary>
/// Button with the two Fluent appearances the launcher uses and a corner radius
/// that can be set per instance. The look lives in Themes/Controls.xaml.
/// </summary>
public class BagaButton : Button
{
    public static readonly DependencyProperty AppearanceProperty = DependencyProperty.Register(
        nameof(Appearance),
        typeof(ButtonAppearance),
        typeof(BagaButton),
        new PropertyMetadata(ButtonAppearance.Secondary));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius),
        typeof(CornerRadius),
        typeof(BagaButton),
        new PropertyMetadata(new CornerRadius(6)));

    public ButtonAppearance Appearance
    {
        get => (ButtonAppearance)GetValue(AppearanceProperty);
        set => SetValue(AppearanceProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }
}
