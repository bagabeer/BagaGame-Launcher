using System;
using System.IO;
using System.Runtime.InteropServices;
using BagaGame.Models;

namespace BagaGame.Services;

/// <summary>
/// Requests a higher Windows timer resolution while a game is running. This is
/// a global system timer, so it is opt-in and always restored after the game.
/// </summary>
public static class TimerResolutionService
{
    private static bool _active;
    private static uint _previous;

    public static void Apply(GameTimerResolution resolution)
    {
        Restore();

        if (resolution == GameTimerResolution.Off)
        {
            return;
        }

        var desired = resolution == GameTimerResolution.HalfMs ? 5000u : 10000u;

        try
        {
            if (NtQueryTimerResolution(out _, out _, out var current) != 0)
            {
                return;
            }

            var status = NtSetTimerResolution(desired, true, out var actual);
            if (status != 0)
            {
                Log($"timer request failed status={status}");
                return;
            }

            _previous = current;
            _active = true;
            Log($"timer {desired / 10000.0:0.###} ms requested, actual {actual / 10000.0:0.###} ms");
        }
        catch
        {
            // Timer resolution is optional and never blocks a launch.
        }
    }

    public static void Restore()
    {
        if (!_active)
        {
            return;
        }

        _active = false;

        try
        {
            NtSetTimerResolution(_previous, true, out _);
            Log("timer restored");
        }
        catch
        {
            // The process is exiting or the timer was already reset.
        }
    }

    private static void Log(string message)
    {
        try
        {
            var directory = AppPaths.DataDirectory;
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "performance.log"),
                DateTime.Now.ToString("u") + "\t" + message + Environment.NewLine);
        }
        catch
        {
            // Diagnostics never block a launch.
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(
        out uint minimumResolution,
        out uint maximumResolution,
        out uint currentResolution);

    [DllImport("ntdll.dll")]
    private static extern int NtSetTimerResolution(
        uint desiredResolution,
        bool setResolution,
        out uint actualResolution);
}
