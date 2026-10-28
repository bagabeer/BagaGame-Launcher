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
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    // A game may need a moment to appear (Steam starts the client first), so a
    // launch that never shows a process is forgotten after this long.
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(70);

    // Once seen, the process must be gone for this long before tracking stops;
    // this tolerates a game that briefly restarts itself.
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(50);

    private readonly LibraryStore _store;
    private readonly GameLibraryService _library;
    private readonly System.Threading.Timer _timer;
    private readonly object _lock = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    private sealed class Session
    {
        public required Game Game { get; init; }

        public string? Executable { get; set; }

        public bool Resolved { get; set; }

        public DateTime Started { get; init; }

        public DateTime LastTick { get; set; }

        public DateTime? LastSeen { get; set; }
    }

    /// <summary>Raised (on the UI thread) with the new total for one game id.</summary>
    public event Action<string, long>? PlaytimeChanged;

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

            if (IsRunning(session))
            {
                var elapsed = now - session.LastTick;
                session.LastTick = now;
                session.LastSeen = now;

                var seconds = (long)elapsed.TotalSeconds;
                if (seconds > 0)
                {
                    Report(id, seconds);
                }

                continue;
            }

            // Not running: stop watching once it has been gone long enough, or
            // was never seen at all.
            var gone = session.LastSeen is DateTime seen
                ? now - seen > StopGrace
                : now - session.Started > StartupGrace;

            if (gone)
            {
                lock (_lock)
                {
                    _sessions.Remove(id);
                }
            }
        }
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

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply();
            return;
        }

        try
        {
            dispatcher.Invoke(Apply);
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
                        // Elevated or protected process: fall back to the name.
                    }

                    if (string.IsNullOrWhiteSpace(path) ||
                        string.Equals(path, executable, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(installPath) &&
                         path.StartsWith(installPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
                catch
                {
                    return true;
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
        var now = DateTime.UtcNow;
        foreach (var session in sessions)
        {
            if (session.LastSeen is null)
            {
                // A launch that never produced a process.
                continue;
            }

            var seconds = (long)(now - session.LastTick).TotalSeconds;
            if (seconds <= 0)
            {
                continue;
            }

            try
            {
                _store.SetPlaytime(session.Game.Id, _store.GetPlaytime(session.Game.Id) + seconds);
            }
            catch
            {
                // Best effort at shutdown.
            }
        }
    }
}