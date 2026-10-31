using System.Windows;
using System.Windows.Controls;

namespace BagaGame.Controls;

/// <summary>
/// A text element that renders one Segoe icon by name. The names match the
/// WPF-UI symbols the XAML used before, so markup stayed readable after the
/// dependency was dropped.
/// </summary>
public sealed class SymbolIcon : TextBlock
{
    /// <summary>Segoe Fluent Icons is Windows 11; MDL2 covers Windows 10.</summary>
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(
        nameof(Symbol),
        typeof(string),
        typeof(SymbolIcon),
        new PropertyMetadata(string.Empty, OnSymbolChanged));

    private static readonly IReadOnlyDictionary<string, string> Glyphs = new Dictionary<string, string>(
        StringComparer.OrdinalIgnoreCase)
    {
        ["Play24"] = "\uE768",
        ["Games24"] = "\uE7FC",
        ["Dismiss24"] = "\uE711",
        ["Search24"] = "\uE721",
        ["Globe24"] = "\uE774",
        ["Folder24"] = "\uE8B7",
        ["WeatherSunny24"] = "\uE706",
        ["ArrowDownload24"] = "\uE896",
        ["EyeOff24"] = "\uED1A",
        ["Library24"] = "\uE8F1",
        ["ChevronRight24"] = "\uE76C",
        ["ChevronDown24"] = "\uE70D",
        ["Settings24"] = "\uE713",
        ["List24"] = "\uEA37",
        ["Grid24"] = "\uE80A",
        ["Add24"] = "\uE710",
        ["Picture24"] = "\uE8B9",
        ["ArrowClockwise24"] = "\uE72C",
    };

    public SymbolIcon()
    {
        FontFamily = new System.Windows.Media.FontFamily(IconFont);
        FontSize = 20;
        TextAlignment = TextAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
        IsHitTestVisible = false;
    }

    public string Symbol
    {
        get => (string)GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    private static void OnSymbolChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var icon = (SymbolIcon)d;
        var name = e.NewValue as string ?? string.Empty;

        // An unknown name renders as nothing rather than as a box.
        icon.Text = Glyphs.TryGetValue(name, out var glyph) ? glyph : string.Empty;
    }
}
