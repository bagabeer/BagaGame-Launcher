using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
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

        // Any unhandled UI exception is written next to the library, so a user
        // report can point at the exact stack instead of "it just closed".
        DispatcherUnhandledException += (_, args) => LogCrash(args.Exception);

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

        // The splash covers the short gap while the library scans, never takes
        // focus, and fades out on its own. The library window is shown only
        // after the splash disappeared, so it cannot pop up behind the logo.
        var splash = new Views.SplashWindow();
        splash.Show();

        var window = new MainWindow(store);
        MainWindow = window;

        var shown = false;
        void ShowLibrary()
        {
            if (shown)
            {
                return;
            }

            shown = true;
            window.Opacity = 1;
            window.Show();
            window.Activate();

            // A window created behind a topmost splash has no foreground
            // rights; the short topmost flip brings it forward.
            window.Topmost = true;
            window.Topmost = false;

            // The library fades in instead of popping up right after the logo.
            var fade = new System.Windows.Media.Animation.DoubleAnimation(
                0,
                1,
                TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
                },
                FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
            };

            fade.Completed += (_, _) => window.Opacity = 1;
            window.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        splash.Closed += (_, _) => ShowLibrary();
        splash.CloseAfter(TimeSpan.FromMilliseconds(1250));

        // Safety net: if the splash never reports Closed, the library still
        // appears a few seconds later.
        var fallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        fallback.Tick += (_, _) =>
        {
            fallback.Stop();
            ShowLibrary();
        };
        fallback.Start();
    }

    private static void LogCrash(Exception exception)
    {
        try
        {
            var directory = AppPaths.DataDirectory;
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, "crash.log");
            var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}{Environment.NewLine}{Environment.NewLine}";

            File.AppendAllText(path, text);

            // Keep the log from growing without a bound.
            var info = new FileInfo(path);
            if (info.Length > 512 * 1024)
            {
                File.WriteAllText(path, text);
            }
        }
        catch
        {
            // Logging must never be the reason the app fails to start.
        }
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
