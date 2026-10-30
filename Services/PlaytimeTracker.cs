using System.Diagnostics;
using System.IO;
using BagaGame.Models;

namespace BagaGame.Services;

/// <summary>
/// Counts how long a game has been played. A game is watched after it was
/// started from the launcher: every <see cref="PollInterval"/> the tracker
/// checks whether a matching process is still alive and reports the grown
/// total. The library keeps the number (see <see cref="LibraryStore"/>).
/// </summary>
public sealed class PlaytimeTracker : IDisposable
{
    // Fast enough to notice that the game closed and to bring the launcher
    // back from the tray, but the settings file is still written only every
    // ReportInterval seconds.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(15);

    // A game may need a moment to appear (Steam starts the client first), so a
    // launch that never shows a process is forgotten after this long.
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(70);

    // Once seen, the process must be gone for this long before tracking stops;
    // this tolerates a game that briefly restarts itself.
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(6);

    private readonly LibraryStore _store;
    private readonly GameLibraryService _library;
    private readonly System.Threading.Timer _timer;
    private readonly object _lock = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);

    private int _tickRunning;
    private volatile bool _disposed;

    private sealed class Session
    {
        public required Game Game { get; init; }

        public string? Executable { get; set; }

        public bool Resolved { get; set; }

        public DateTime Started { get; init; }

        public DateTime LastTick { get; set; }

        public DateTime? LastSeen { get; set; }

        /// <summary>Seconds counted but not yet written to the settings file.</summary>
        public double PendingSeconds { get; set; }
    }

    /// <summary>Raised (on the UI thread) with the new total for one game id.</summary>
    public event Action<string, long>? PlaytimeChanged;

    /// <summary>
    /// Raised (on the UI thread) when a tracked game is no longer running, or
    /// never appeared at all. The window uses it to come back from the tray.
    /// </summary>
    public event Action<string>? GameStopped;

    public PlaytimeTracker(LibraryStore store, GameLibraryService library)
    {
        _store = store;
        _library = library;

        _timer = new System.Threading.Timer(
            _ => Tick(),
            null,
            PollInterval,
            PollInterval);
    }

    /// <summary>Starts watching a game that was just launched from the launcher.</summary>
    public void Track(Game game)
    {
        if (_disposed || game is null || string.IsNullOrWhiteSpace(game.Id))
        {
            return;
        }

        var now = DateTime.UtcNow;

        lock (_lock)
        {
            if (_sessions.ContainsKey(game.Id))
            {
                return;
            }

            _sessions[game.Id] = new Session
            {
                Game = game,
                Started = now,
                LastTick = now,
            };
        }
    }

    private void Tick()
    {
        // System.Threading.Timer overlaps callbacks when a tick takes longer
        // than the interval. Never let two ticks count the same session.
        if (Interlocked.Exchange(ref _tickRunning, 1) != 0)
        {
            return;
        }

        try
        {
            TickCore();
        }
        finally
        {
            Volatile.Write(ref _tickRunning, 0);
        }
    }

    private void TickCore()
    {
        if (_disposed)
        {
            return;
        }

        List<Session> sessions;
        lock (_lock)
        {
            sessions = _sessions.Values.ToList();
        }

        if (sessions.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;

        foreach (var session in sessions)
        {
            var id = session.Game.Id;

            if (!session.Resolved)
            {
                try
                {
                    session.Executable = _library.ResolveLaunchExecutable(session.Game);
                }
                catch
                {
                    session.Executable = null;
                }

                session.Resolved = true;
            }

            // The interval is measured from the previous tick whether the game
            // was running or not, so a pause between two sessions is not
            // counted as playtime.
            var elapsed = now - session.LastTick;
            session.LastTick = now;

            if (IsRunning(session))
            {
                session.LastSeen = now;
                session.PendingSeconds += Math.Max(0, elapsed.TotalSeconds);
                FlushIfDue(session, force: false);
                continue;
            }

            // Not running: stop watching once it has been gone long enough, or
            // was never seen at all.
            var gone = session.LastSeen is DateTime seen
                ? now - seen > StopGrace
                : now - session.Started > StartupGrace;

            if (!gone)
            {
                continue;
            }

            FlushIfDue(session, force: true);

            lock (_lock)
            {
                _sessions.Remove(id);
            }

            RaiseGameStopped(id);
        }
    }

    /// <summary>Writes accumulated seconds at most every ReportInterval.</summary>
    private void FlushIfDue(Session session, bool force)
    {
        if (!force && session.PendingSeconds < ReportInterval.TotalSeconds)
        {
            return;
        }

        var seconds = (long)session.PendingSeconds;
        if (seconds <= 0)
        {
            return;
        }

        session.PendingSeconds -= seconds;
        Report(session.Game.Id, seconds);
    }

    /// <summary>Adds time to the game's total and raises it on the UI thread.</summary>
    private void Report(string gameId, long addedSeconds)
    {
        var handler = PlaytimeChanged;
        if (handler is null)
        {
            return;
        }

        // The total is read and written on the UI thread so the settings file
        // never sees two threads at once.
        void Apply()
        {
            var total = _store.GetPlaytime(gameId) + addedSeconds;
            handler(gameId, total);
        }

        InvokeOnUi(Apply);
    }

    private void RaiseGameStopped(string gameId)
    {
        var handler = GameStopped;
        if (handler is null)
        {
            return;
        }

        InvokeOnUi(() => handler(gameId));
    }

    private static void InvokeOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            dispatcher.Invoke(action);
        }
        catch
        {
            // The window is closing; the persisted value stays as it was.
        }
    }

    private static bool IsRunning(Session session)
    {
        var executable = session.Executable;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(executable);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(name);
        }
        catch
        {
            return false;
        }

        var installPath = session.Game.InstallPath;

        try
        {
            foreach (var process in processes)
            {
                try
                {
                    string? path = null;
                    try
                    {
                        path = process.MainModule?.FileName;
                    }
                    catch
                    {
                        // An inaccessible process cannot be matched reliably by
                        // its name alone: another game may use the same name.
                    }

                    if (!string.IsNullOrWhiteSpace(path) &&
                        MatchesGameProcess(path, executable, installPath))
                    {
                        return true;
                    }
                }
                catch
                {
                    // The process may exit during inspection. Keep checking the
                    // remaining candidates instead of assuming a match.
                }
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        return false;
    }

    private static bool MatchesGameProcess(string path, string executable, string? installPath)
    {
        try
        {
            path = Path.GetFullPath(path);

            if (string.Equals(
                    path,
                    Path.GetFullPath(executable),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(installPath))
            {
                return false;
            }

            // A prefix such as C:\Games\Foo must not match C:\Games\Foobar.
            var root = Path.GetFullPath(installPath).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);

            return path.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();

        List<Session> sessions;
        lock (_lock)
        {
            sessions = _sessions.Values.ToList();
            _sessions.Clear();
        }

        // The launcher is going away while the game may still run: keep the
        // seconds of the current, not yet reported interval.
        foreach (var session in sessions)
        {
            var seconds = (long)session.PendingSeconds;
            if (seconds <= 0)
            {
                continue;
            }

            try
            {
                _store.SetPlaytime(
                    session.Game.Id,
                    _store.GetPlaytime(session.Game.Id) + seconds);
            }
            catch
            {
                // Best effort at shutdown.
            }
        }
    }
}
