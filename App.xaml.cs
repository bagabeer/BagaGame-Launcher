using System.Windows;
using BagaGame.Services;

namespace BagaGame;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One store instance is shared with the window so settings never race
        // each other when the theme is switched.
        var store = new LibraryStore();

        // Dark or light theme + Mica backdrop (Mica is skipped on Windows 10).
        ThemeService.Apply(store.IsLightTheme ? AppTheme.Light : AppTheme.Dark);

        var window = new MainWindow(store);
        MainWindow = window;
        window.Show();
    }
}
