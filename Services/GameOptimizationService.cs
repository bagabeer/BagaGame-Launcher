using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using BagaGame.Models;

namespace BagaGame.Services;

/// <summary>
/// Applies the universal launch profile to a game process. All operations are
/// documented Win32 calls: no injection, no patches and no system-wide tweaks.
/// Unsupported CPU features are silently skipped.
/// </summary>
public static class GameOptimizationService
{
    private const uint ProcessSetInformation = 0x0200;
    private const uint ProcessQueryInformation = 0x0400;

    private const uint NormalPriorityClass = 0x20;
    private const uint AboveNormalPriorityClass = 0x8000;
    private const uint HighPriorityClass = 0x80;

    private const int ProcessPowerThrottling = 4;
    private const uint PowerThrottlingExecutionSpeed = 0x1;

    /// <summary>Returns true when at least one property was accepted.</summary>
    public static bool ApplyToProcess(
        Process process,
        GameBoostSettings settings,
        CpuTopology topology)
    {
        if (!settings.Enabled || process is null)
        {
            return false;
        }

        var handle = OpenProcess(
            ProcessSetInformation | ProcessQueryInformation,
            false,
            process.Id);

        if (handle == IntPtr.Zero)
        {
            Log("OpenProcess failed for PID " + process.Id);
            return false;
        }

        var applied = false;

        try
        {
            var cpuIds = SelectCpuSets(settings, topology);
            if (cpuIds is { Count: > 0 })
            {
                var ids = cpuIds.ToArray();
                var setsApplied = SetProcessDefaultCpuSets(handle, ids, (uint)ids.Length);

                // CPU sets only set the default for new threads. Some games
                // already have threads on other cores, so also apply a hard
                // affinity mask when the selected cores fit one group.
                var affinityApplied = TrySetAffinity(handle, ids, topology);

                if (setsApplied || affinityApplied)
                {
                    applied = true;
                    Log(
                        $"CPU sets: {ids.Length} for PID {process.Id}, " +
                        $"sets={setsApplied}, affinity={affinityApplied}");
                }
                else
                {
                    Log("CPU affinity rejected for PID " + process.Id);
                }
            }

            var priority = settings.Priority switch
            {
                GameProcessPriority.High => HighPriorityClass,
                GameProcessPriority.AboveNormal => AboveNormalPriorityClass,
                _ => NormalPriorityClass,
            };

            if (SetPriorityClass(handle, priority))
            {
                applied = true;
            }

            if (settings.DisableEcoQos)
            {
                var state = new ProcessPowerThrottlingState
                {
                    Version = 1,
                    ControlMask = PowerThrottlingExecutionSpeed,
                    StateMask = 0,
                };

                if (!SetProcessInformation(
                        handle,
                        ProcessPowerThrottling,
                        ref state,
                        (uint)Marshal.SizeOf<ProcessPowerThrottlingState>()))
                {
                    Log("Power throttling rejected for PID " + process.Id);
                }
                else
                {
                    applied = true;
                }
            }
        }
        catch
        {
            // A protected or already closed process is simply skipped.
        }
        finally
        {
            CloseHandle(handle);
        }

        return applied;
    }

    /// <summary>
    /// Steam and third-party launchers create the real game process later. This
    /// short watcher applies the same profile as soon as that process appears.
    /// </summary>
    public static void WatchForProcess(
        string? executablePath,
        GameBoostSettings settings,
        CpuTopology topology)
    {
        if (!settings.Enabled || string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        var name = Path.GetFileNameWithoutExtension(executablePath);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var started = DateTime.UtcNow;

            while (DateTime.UtcNow - started < TimeSpan.FromSeconds(60))
            {
                try
                {
                    foreach (var process in Process.GetProcessesByName(name))
                    {
                        try
                        {
                            if (process.StartTime >= started.AddSeconds(-5) &&
                                ApplyToProcess(process, settings, topology))
                            {
                                ReapplyAfterDelay(
                                    process.Id,
                                    settings,
                                    topology,
                                    TimeSpan.FromSeconds(2));
                                return;
                            }
                        }
                        finally
                        {
                            process.Dispose();
                        }
                    }
                }
                catch
                {
                    // The process may exit between enumeration and access.
                }

                await Task.Delay(250).ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// Some games reset their own priority during startup. Apply the profile
    /// once more after the initial load, without touching the CPU mask.
    /// </summary>
    public static void ReapplyAfterDelay(
        int processId,
        GameBoostSettings settings,
        CpuTopology topology,
        TimeSpan delay)
    {
        if (processId <= 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                using var process = Process.GetProcessById(processId);
                ApplyToProcess(process, settings, topology);
            }
            catch
            {
                // The game exited or is protected; the first pass is enough.
            }
        });
    }

    public static void SetLauncherBackground(bool enabled)
    {
        try
        {
            Process.GetCurrentProcess().PriorityClass = enabled
                ? ProcessPriorityClass.BelowNormal
                : ProcessPriorityClass.Normal;
        }
        catch
        {
            // The launcher priority is a best-effort extra.
        }
    }

    private static IReadOnlyList<uint>? SelectCpuSets(
        GameBoostSettings settings,
        CpuTopology topology)
    {
        return settings.CpuMode switch
        {
            CpuOptimizationMode.PerformanceCores when topology.CanForcePerformanceCores =>
                topology.PerformanceCpuSetIds,
            CpuOptimizationMode.EfficiencyCores when topology.CanForceEfficiencyCores =>
                topology.EfficiencyCpuSetIds,
            CpuOptimizationMode.PreferredL3Domain when topology.CanSelectL3Domain =>
                topology.PreferredL3Domain?.CpuSetIds,
            _ => null,
        };
    }

    private static bool TrySetAffinity(
        IntPtr handle,
        IReadOnlyCollection<uint> ids,
        CpuTopology topology)
    {
        ulong mask = 0;

        foreach (var core in topology.Cores)
        {
            if (core.Group == 0 &&
                core.LogicalProcessorIndex < 64 &&
                ids.Contains(core.CpuSetId))
            {
                mask |= 1UL << core.LogicalProcessorIndex;
            }
        }

        return mask != 0 && SetProcessAffinityMask(handle, (UIntPtr)mask);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(
        IntPtr process,
        uint priorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessDefaultCpuSets(
        IntPtr process,
        uint[] cpuSetIds,
        uint cpuSetIdCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessAffinityMask(
        IntPtr process,
        UIntPtr processAffinityMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        IntPtr process,
        int informationClass,
        ref ProcessPowerThrottlingState information,
        uint informationLength);
}
