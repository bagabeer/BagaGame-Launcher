using System.Windows;

namespace BagaGame.Services;

public enum AppTheme
{
    Dark,
    Light,
}

/// <summary>
/// Swaps the BagaGame palette dictionary and repaints the native window chrome.
/// The palette is a merged dictionary, so replacing it updates every
/// <c>DynamicResource</c> in the window immediately.
/// </summary>
public static class ThemeService
{
    private static readonly string[] PaletteFiles = { "Dark.xaml", "Light.xaml" };

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static void Apply(AppTheme theme)
    {
        SwapPalette(theme);
        Current = theme;

        // The title bar is drawn by DWM, not by WPF, so it needs its own nudge.
        foreach (Window window in Application.Current.Windows)
        {
            WindowEffects.SetDarkTitleBar(window, theme == AppTheme.Dark);
        }
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
            var source = dictionaries[index].Source?.OriginalString;
            if (source is null || !PaletteFiles.Any(name => source.EndsWith(name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            dictionaries[index] = replacement;
            return;
        }

        dictionaries.Add(replacement);
    }
}
