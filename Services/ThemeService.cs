using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace BagaGame.Services;

public enum AppTheme
{
    Dark,
    Light,
}

/// <summary>
/// Applies the WPF-UI theme together with the BagaGame palette dictionary.
/// The palette is a merged dictionary, so swapping it updates every
/// <c>DynamicResource</c> in the window immediately.
/// </summary>
public static class ThemeService
{
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static void Apply(AppTheme theme)
    {
        var uiTheme = theme == AppTheme.Light
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark;

        // The accent must be written *before* the theme dictionary is swapped:
        // WPF-UI brushes like ToggleSwitchFillOn bind to SystemAccentColorPrimary
        // and freeze at dictionary load, so the reverse order keeps stale colours.
        var accent = theme == AppTheme.Light
            ? Color.FromRgb(0x3F, 0x7A, 0x16)
            : Color.FromRgb(0xA5, 0xF2, 0x6B);

        ApplicationAccentColorManager.Apply(accent, uiTheme);

        // WPF-UI control colours (toggle switches, buttons, context menus).
        // updateAccent must be false: otherwise WPF-UI resets the accent to the
        // Windows system colour while loading the theme dictionary.
        ApplicationThemeManager.Apply(
            uiTheme,
            WindowBackdropType.Mica,
            updateAccent: false);

        SwapPalette(theme);
        Current = theme;
    }

    private static void SwapPalette(AppTheme theme)
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        var dictionaries = application.Resources.MergedDictionaries;
        var fileName = theme == AppTheme.Light ? "Light.xaml" : "Dark.xaml";

        var replacement = new ResourceDictionary
        {
            Source = new Uri($"/BagaGame;component/Themes/{fileName}", UriKind.Relative),
        };

        for (var index = 0; index < dictionaries.Count; index++)
        {
            var source = dictionaries[index].Source;
            if (source is null ||
                !source.OriginalString.Contains("Themes/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            dictionaries[index] = replacement;
            return;
        }

        dictionaries.Add(replacement);
    }
}
