using System.Windows;
using BagaGame.Services;

namespace BagaGame;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A second process would overwrite the same library.json. The mutex is
        // local to the Windows session because each user has a separate store.
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\BagaGame.SingleInstance",
            createdNew: out _ownsSingleInstanceMutex);

        if (!_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            MessageBox.Show(
                "BagaGame уже запущен.",
                "BagaGame",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // One store instance is shared with the window so settings never race
        // each other when the theme is switched.
        var store = new LibraryStore();

        // Dark or light theme + Mica backdrop (Mica is skipped on Windows 10).
        ThemeService.Apply(store.IsLightTheme ? AppTheme.Light : AppTheme.Dark);

        var window = new MainWindow(store);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
            _ownsSingleInstanceMutex = false;
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }
}
