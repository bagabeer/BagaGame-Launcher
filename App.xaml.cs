using System.Threading;
using System.Windows;
using BagaGame.Services;

namespace BagaGame;

public partial class App : Application
{
    // A second process would race the same library.json. The mutex is local to
    // the Windows session because each user has a separate data folder.
    private const string MutexName = @"Local\BagaGame.SingleInstance";
    private const string ActivateEventName = @"Local\BagaGame.Activate";

    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _activateSignal;
    private RegisteredWaitHandle? _activateWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // .NET Framework otherwise negotiates the system default, which may
        // still include obsolete TLS versions on old Windows builds.
        System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;

        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out _ownsMutex);

        if (!_ownsMutex)
        {
            // Another copy is already running: ask it to show its window and
            // leave without touching the settings file.
            SignalRunningInstance();
            Shutdown();
            return;
        }

        ListenForActivation();

        // One store instance is shared with the window so settings never race
        // each other when the theme or the language is switched.
        var store = new LibraryStore();

        // The language is applied before the window is created, so the first
        // frame already uses the saved language instead of flashing Russian.
        Loc.Apply(store.Language ?? Loc.DetectSystemLanguage());

        // Dark or light theme + Mica backdrop (Mica is skipped on Windows 10).
        ThemeService.Apply(store.IsLightTheme ? AppTheme.Light : AppTheme.Dark);

        var window = new MainWindow(store);
        MainWindow = window;
        window.Show();
    }

    private void ListenForActivation()
    {
        try
        {
            _activateSignal = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                ActivateEventName);

            _activateWait = ThreadPool.RegisterWaitForSingleObject(
                _activateSignal,
                (_, _) => Dispatcher.BeginInvoke(new Action(BringToFront)),
                state: null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch
        {
            // Without the signal a second launch simply exits; the running
            // window can still be opened from the tray icon.
        }
    }

    private void BringToFront()
        => (MainWindow as MainWindow)?.ShowFromExternalLaunch();

    private static void SignalRunningInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var signal))
            {
                using (signal)
                {
                    signal.Set();
                }
            }
        }
        catch
        {
            // The other instance is closing; nothing to activate.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateWait?.Unregister(null);
        _activateWait = null;

        _activateSignal?.Dispose();
        _activateSignal = null;

        if (_ownsMutex)
        {
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch
            {
                // The mutex was already released while shutting down.
            }

            _ownsMutex = false;
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        base.OnExit(e);
    }
}
