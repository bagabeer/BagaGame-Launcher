using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BagaGame.Services;

public sealed class CleanupReport
{
    public List<string> Errors { get; } = new();

    public long FreedBytes { get; set; }

    public int FreedFiles { get; set; }
}

/// <summary>
/// Safe housekeeping for everything the launcher creates: temporary extraction
/// folders, downloaded archives, old emulator versions and stale logs. User
/// data (saves, achievements, covers, library, original DLL backups) is never
/// touched here.
/// </summary>
public static class CleanupService
{
    private const int KeepVersions = 3;
    private const int LogDays = 30;
    private const long LogLimit = 1024 * 1024;

    private sealed record Candidate(string Path, bool Directory);

    public static Task<CleanupReport> RunAsync(CancellationToken cancellationToken)
        => Task.Run(() => Run(cancellationToken), cancellationToken);

    public static CleanupReport Run(CancellationToken cancellationToken)
    {
        var report = new CleanupReport();

        Delete(report, TempCandidates(), cancellationToken);
        Delete(report, ArchiveCandidates(), cancellationToken);
        Delete(report, OldVersionCandidates(), cancellationToken);
        Delete(report, LogCandidates(), cancellationToken);

        WriteLog(report);
        return report;
    }

    private static void Delete(
        CleanupReport report,
        IReadOnlyList<Candidate> candidates,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        long bytes = 0;
        var files = 0;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (candidate.Directory)
                {
                    var size = SizeOfDirectory(candidate.Path, cancellationToken);
                    Directory.Delete(candidate.Path, true);
                    bytes += size.Bytes;
                    files += size.Files;
                }
                else
                {
                    var size = new FileInfo(candidate.Path).Length;
                    File.Delete(candidate.Path);
                    bytes += size;
                    files++;
                }
            }
            catch
            {
                report.Errors.Add(candidate.Path);
            }
        }

        report.FreedBytes += bytes;
        report.FreedFiles += files;
    }

    // ------------------------------------------------------------------
    // Candidates
    // ------------------------------------------------------------------

    private static IReadOnlyList<Candidate> TempCandidates()
    {
        var result = new List<Candidate>();
        var emulators = Path.Combine(AppPaths.DataDirectory, "emulators");

        foreach (var directory in SafeDirectories(emulators, "_extract", SearchOption.AllDirectories))
        {
            result.Add(new Candidate(directory, true));
        }

        foreach (var file in SafeFiles(AppPaths.DataDirectory, "*.part", SearchOption.AllDirectories))
        {
            result.Add(new Candidate(file, false));
        }

        return result;
    }

    private static IReadOnlyList<Candidate> ArchiveCandidates()
    {
        var cache = Path.Combine(AppPaths.DataDirectory, "emulators", "_cache");
        return SafeFiles(cache, "*.7z", SearchOption.TopDirectoryOnly)
            .Concat(SafeFiles(cache, "*.zip", SearchOption.TopDirectoryOnly))
            .Select(path => new Candidate(path, false))
            .ToList();
    }

    private static IReadOnlyList<Candidate> OldVersionCandidates()
    {
        var result = new List<Candidate>();
        var root = Path.Combine(AppPaths.DataDirectory, "emulators");

        foreach (var family in SafeDirectories(root))
        {
            if (string.Equals(Path.GetFileName(family), "_cache", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var source in SafeDirectories(family))
            {
                var versions = SafeDirectories(source)
                    .OrderByDescending(LastWrite)
                    .Skip(KeepVersions)
                    .ToList();

                foreach (var version in versions)
                {
                    result.Add(new Candidate(version, true));
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<Candidate> LogCandidates()
    {
        var result = new List<Candidate>();
        var cutoff = DateTime.UtcNow.AddDays(-LogDays);

        foreach (var file in SafeFiles(AppPaths.DataDirectory, "*.log", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc < cutoff || info.Length > LogLimit)
                {
                    result.Add(new Candidate(file, false));
                }
            }
            catch
            {
                // A locked log is skipped.
            }
        }

        foreach (var file in SafeFiles(AppPaths.DataDirectory, "*.log.1", SearchOption.TopDirectoryOnly))
        {
            result.Add(new Candidate(file, false));
        }

        return result;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static (long Bytes, int Files) SizeOfDirectory(string path, CancellationToken cancellationToken)
    {
        long bytes = 0;
        var files = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    bytes += new FileInfo(file).Length;
                    files++;
                }
                catch
                {
                    // A vanished file only reduces the estimate.
                }
            }
        }
        catch
        {
            // An unreadable folder keeps the partial size.
        }

        return (bytes, files);
    }

    private static DateTime LastWrite(string path)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static IEnumerable<string> SafeDirectories(string root, string? pattern = null, SearchOption option = SearchOption.TopDirectoryOnly)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return Array.Empty<string>();
        }

        try
        {
            return pattern is null
                ? Directory.GetDirectories(root, "*", option)
                : Directory.GetDirectories(root, pattern, option);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeFiles(string root, string pattern, SearchOption option)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory.GetFiles(root, pattern, option);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static void WriteLog(CleanupReport report)
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDirectory, "cleanup.log");

            if (File.Exists(path) && new FileInfo(path).Length > LogLimit)
            {
                var previous = path + ".1";
                if (File.Exists(previous))
                {
                    File.Delete(previous);
                }

                File.Move(path, previous);
            }

            var line = string.Format(
                "{0:u}\t{1} bytes\t{2} files\t{3} errors",
                DateTime.UtcNow,
                report.FreedBytes,
                report.FreedFiles,
                report.Errors.Count);

            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch
        {
            // Logging never breaks cleanup.
        }
    }
}
