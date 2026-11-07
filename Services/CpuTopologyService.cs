using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BagaGame.Services;

public sealed record CpuCoreInfo(
    int Group,
    int LogicalProcessorIndex,
    int CoreIndex,
    int EfficiencyClass,
    bool IsPerformance,
    uint CpuSetId,
    int SmtCount);

public sealed record CpuL3Domain(
    int Index,
    long SizeBytes,
    IReadOnlyList<uint> CpuSetIds);

/// <summary>
/// A hardware-independent description of the CPU. Intel P/E cores, AMD CCDs
/// and ordinary CPUs all end up in the same model; features that the CPU does
/// not support simply stay empty.
/// </summary>
public sealed class CpuTopology
{
    public string Vendor { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public int PhysicalCores { get; init; }

    public int LogicalProcessors { get; init; }

    public bool IsHybrid { get; init; }

    public IReadOnlyList<CpuCoreInfo> Cores { get; init; } = Array.Empty<CpuCoreInfo>();

    public IReadOnlyList<uint> PerformanceCpuSetIds { get; init; } = Array.Empty<uint>();

    public IReadOnlyList<uint> EfficiencyCpuSetIds { get; init; } = Array.Empty<uint>();

    public IReadOnlyList<CpuL3Domain> L3Domains { get; init; } = Array.Empty<CpuL3Domain>();

    public bool CanForcePerformanceCores => IsHybrid && PerformanceCpuSetIds.Count > 0;

    public bool CanForceEfficiencyCores => IsHybrid && EfficiencyCpuSetIds.Count > 0;

    public bool CanSelectL3Domain => L3Domains.Count > 1 &&
        Vendor.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0;

    public CpuL3Domain? PreferredL3Domain => L3Domains
        .OrderByDescending(domain => domain.SizeBytes)
        .ThenByDescending(domain => domain.CpuSetIds.Count)
        .FirstOrDefault();
}

/// <summary>
/// Reads the Windows CPU topology without vendor databases or hard-coded model
/// lists. Efficiency classes describe Intel/ARM hybrids; L3 domains describe
/// AMD CCDs. When an API is unavailable the caller gets a safe fallback.
/// </summary>
public static class CpuTopologyService
{
    private static readonly Lazy<CpuTopology> Cached = new(DetectCore);

    public static CpuTopology Detect() => Cached.Value;

    private static CpuTopology DetectCore()
    {
        try
        {
            var sets = ReadCpuSets();
            if (sets.Count == 0)
            {
                return Fallback();
            }

            var vendor = ReadVendor();
            var classes = sets
                .Select(set => (int)set.EfficiencyClass)
                .Distinct()
                .OrderByDescending(value => value)
                .ToList();

            // AMD normally reports one class; requiring Intel/ARM-style
            // classes here avoids a fake P-core mode on ordinary Ryzen CPUs.
            var hybrid = classes.Count > 1 &&
                         vendor.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) < 0;
            var performanceClass = classes.FirstOrDefault();

            if (hybrid)
            {
                // SMT is a reliable secondary signal: on Intel hybrids the P
                // cores have two logical processors and the E cores do not.
                var smtClasses = new HashSet<int>();
                foreach (var group in sets.GroupBy(set => (set.Group, set.CoreIndex)))
                {
                    if (group.Count() > 1)
                    {
                        foreach (var set in group)
                        {
                            smtClasses.Add(set.EfficiencyClass);
                        }
                    }
                }

                if (smtClasses.Count == 1)
                {
                    performanceClass = smtClasses.First();
                }
            }

            var cores = sets
                .Select(set =>
                {
                    var smt = sets.Count(other =>
                        other.Group == set.Group &&
                        other.CoreIndex == set.CoreIndex);
                    return new CpuCoreInfo(
                        set.Group,
                        set.LogicalProcessorIndex,
                        set.CoreIndex,
                        set.EfficiencyClass,
                        hybrid && set.EfficiencyClass == performanceClass,
                        set.Id,
                        smt);
                })
                .ToList();

            var performanceIds = cores
                .Where(core => core.IsPerformance)
                .Select(core => core.CpuSetId)
                .ToArray();

            var efficiencyIds = cores
                .Where(core => hybrid && !core.IsPerformance)
                .Select(core => core.CpuSetId)
                .ToArray();

            var l3Domains = ReadL3Domains(cores);

            var physical = cores
                .Select(core => (core.Group, core.CoreIndex))
                .Distinct()
                .Count();

            return new CpuTopology
            {
                Vendor = vendor,
                Model = ReadModel(),
                PhysicalCores = physical,
                LogicalProcessors = cores.Count,
                IsHybrid = hybrid,
                Cores = cores,
                PerformanceCpuSetIds = performanceIds,
                EfficiencyCpuSetIds = efficiencyIds,
                L3Domains = l3Domains,
            };
        }
        catch
        {
            return Fallback();
        }
    }

    private static CpuTopology Fallback()
    {
        var logical = Math.Max(1, Environment.ProcessorCount);

        return new CpuTopology
        {
            Vendor = ReadVendor(),
            Model = ReadModel(),
            PhysicalCores = logical,
            LogicalProcessors = logical,
            IsHybrid = false,
            Cores = Array.Empty<CpuCoreInfo>(),
            PerformanceCpuSetIds = Array.Empty<uint>(),
            EfficiencyCpuSetIds = Array.Empty<uint>(),
            L3Domains = Array.Empty<CpuL3Domain>(),
        };
    }

    private static List<CpuSet> ReadCpuSets()
    {
        var result = new List<CpuSet>();

        GetSystemCpuSetInformation(IntPtr.Zero, 0, out var needed, IntPtr.Zero, 0);
        if (needed == 0)
        {
            return result;
        }

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetSystemCpuSetInformation(buffer, needed, out var returned, IntPtr.Zero, 0))
            {
                return result;
            }

            var limit = Math.Min(returned, needed);
            var offset = 0;
            while (offset + 24 <= limit)
            {
                var size = Marshal.ReadInt32(buffer, offset);
                if (size < 24 || offset + size > limit)
                {
                    break;
                }

                var type = Marshal.ReadInt32(buffer, offset + 4);
                if (type == 0)
                {
                    result.Add(new CpuSet(
                        (uint)Marshal.ReadInt32(buffer, offset + 8),
                        (ushort)Marshal.ReadInt16(buffer, offset + 12),
                        Marshal.ReadByte(buffer, offset + 14),
                        Marshal.ReadByte(buffer, offset + 15),
                        Marshal.ReadByte(buffer, offset + 16),
                        Marshal.ReadByte(buffer, offset + 18)));
                }

                offset += size;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    private static IReadOnlyList<CpuL3Domain> ReadL3Domains(
        IReadOnlyList<CpuCoreInfo> cores)
    {
        var byId = cores.ToDictionary(core => core.CpuSetId);
        var result = new List<CpuL3Domain>();

        try
        {
            uint length = 0;
            GetLogicalProcessorInformationEx(RelationCache, IntPtr.Zero, ref length);
            if (length == 0)
            {
                return FallbackL3Domains(cores);
            }

            var allocated = length;
            var buffer = Marshal.AllocHGlobal((int)allocated);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationCache, buffer, ref length))
                {
                    return FallbackL3Domains(cores);
                }

                var limit = Math.Min(length, allocated);
                var offset = 0;
                var index = 0;
                while (offset + 8 <= limit)
                {
                    var relationship = Marshal.ReadInt32(buffer, offset);
                    var size = Marshal.ReadInt32(buffer, offset + 4);
                    if (size <= 0 || offset + size > limit)
                    {
                        break;
                    }

                    if (relationship == RelationCache)
                    {
                        var union = IntPtr.Add(buffer, offset + 8);
                        var unionSize = size - 8;

                        if (unionSize >= 40)
                        {
                            var level = Marshal.ReadByte(union);
                            var cacheSize = (uint)Marshal.ReadInt32(union, 4);
                            var type = Marshal.ReadInt32(union, 8);

                            if (level == 3 && type == 0)
                            {
                                // Windows builds differ slightly in where the
                                // group count sits before the 64-bit masks.
                                // Try both known offsets and keep the first
                                // layout that maps to real processor sets.
                                foreach (var countOffset in new[] { 30, 32 })
                                {
                                    if (countOffset + 2 > unionSize)
                                    {
                                        continue;
                                    }

                                    var groupCount = (ushort)Marshal.ReadInt16(
                                        union,
                                        countOffset);

                                    if (groupCount is < 1 or > 64)
                                    {
                                        continue;
                                    }

                                    var groupMaskOffset = unionSize - groupCount * 16;
                                    if (groupMaskOffset < 24 ||
                                        groupMaskOffset + groupCount * 16 > unionSize)
                                    {
                                        continue;
                                    }

                                    var ids = new HashSet<uint>();

                                    for (var groupIndex = 0; groupIndex < groupCount; groupIndex++)
                                    {
                                        var affinity = IntPtr.Add(
                                            union,
                                            groupMaskOffset + groupIndex * 16);
                                        var mask = (ulong)Marshal.ReadInt64(affinity);
                                        var group = (ushort)Marshal.ReadInt16(affinity, 8);

                                        AddBits(ids, mask, group, byId);
                                    }

                                    if (ids.Count == 0)
                                    {
                                        continue;
                                    }

                                    result.Add(new CpuL3Domain(
                                        index++,
                                        cacheSize,
                                        ids.ToArray()));
                                    break;
                                }
                            }
                        }
                    }

                    offset += size;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return FallbackL3Domains(cores);
        }

        return result.Count > 0 ? result : FallbackL3Domains(cores);
    }

    private static IReadOnlyList<CpuL3Domain> FallbackL3Domains(
        IReadOnlyList<CpuCoreInfo> cores)
    {
        if (cores.Count == 0)
        {
            return Array.Empty<CpuL3Domain>();
        }

        // The cache relationship could not be read: keep one safe domain so
        // the "single CCD" option is not offered without real data.
        return new[]
        {
            new CpuL3Domain(
                0,
                0,
                cores.Select(core => core.CpuSetId).ToArray()),
        };
    }

    private static void AddBits(
        HashSet<uint> ids,
        ulong mask,
        ushort group,
        IReadOnlyDictionary<uint, CpuCoreInfo> byId)
    {
        for (var bit = 0; bit < 64; bit++)
        {
            if ((mask & (1UL << bit)) == 0)
            {
                continue;
            }

            foreach (var core in byId.Values)
            {
                if (core.Group == group &&
                    core.LogicalProcessorIndex == bit)
                {
                    ids.Add(core.CpuSetId);
                }
            }
        }
    }

    private static string ReadVendor()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("VendorIdentifier") is string vendor &&
                !string.IsNullOrWhiteSpace(vendor))
            {
                return vendor.Trim();
            }
        }
        catch
        {
            // The registry is optional.
        }

        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? string.Empty;
    }

    private static string ReadModel()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("ProcessorNameString") is string model &&
                !string.IsNullOrWhiteSpace(model))
            {
                return model.Trim();
            }
        }
        catch
        {
            // The registry is optional.
        }

        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? string.Empty;
    }

    private readonly record struct CpuSet(
        uint Id,
        ushort Group,
        byte LogicalProcessorIndex,
        byte CoreIndex,
        byte LastLevelCacheIndex,
        byte EfficiencyClass);

    private const int RelationCache = 2;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(
        IntPtr information,
        uint bufferLength,
        out uint returnedLength,
        IntPtr process,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationshipType,
        IntPtr buffer,
        ref uint returnedLength);
}
