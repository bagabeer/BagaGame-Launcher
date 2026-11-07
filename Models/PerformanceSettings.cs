namespace BagaGame.Models;

/// <summary>
/// How the game process should be distributed across the available CPU sets.
/// The launcher stores the intent, not a raw affinity mask, so the same profile
/// still works when the user moves to another PC.
/// </summary>
public enum CpuOptimizationMode
{
    Auto,
    PerformanceCores,
    EfficiencyCores,
    PreferredL3Domain,
}

/// <summary>Priority class applied to the game process for its lifetime.</summary>
public enum GameProcessPriority
{
    Normal,
    AboveNormal,
    High,
}

/// <summary>Optional Windows timer resolution request while a game runs.</summary>
public enum GameTimerResolution
{
    Off,
    OneMs,
    HalfMs,
}

/// <summary>Snapshot of the launcher optimisation settings for one launch.</summary>
public sealed record GameBoostSettings(
    bool Enabled,
    CpuOptimizationMode CpuMode,
    GameProcessPriority Priority,
    bool DisableEcoQos,
    bool LauncherBackground,
    GameTimerResolution TimerResolution,
    bool ApplyToChildren);
