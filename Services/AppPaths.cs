using System.IO;

namespace BagaGame.Services;

/// <summary>
/// Chooses where BagaGame keeps its files. A portable build stores everything
/// in <c>BagaGameData</c> next to the executable, so copying the .exe (and that
/// folder) to another machine keeps the library. When the folder is read-only
/// (for example under Program Files) the launcher falls back to
/// <c>%LocalAppData%\BagaGame</c>.
/// </summary>
public static class AppPaths
{
    public const string PortableFolderName = "BagaGameData";

    public static string DataDirectory { get; } = ResolveDataDirectory();

    /// <summary>True when the data lives next to the executable.</summary>
    public static bool IsPortable { get; } = IsPortableDirectory(DataDirectory);

    public static string DefaultCoverCacheDirectory
        => Path.Combine(DataDirectory, "covers");

    private static string ResolveDataDirectory()
    {
        var executableDirectory = AppDomain.CurrentDomain.BaseDirectory;

        if (!string.IsNullOrWhiteSpace(executableDirectory))
        {
            var portable = Path.Combine(executableDirectory, PortableFolderName);

            if (TryPrepare(portable))
            {
                return portable;
            }
        }

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BagaGame");

        TryPrepare(fallback);
        return fallback;
    }

    private static bool TryPrepare(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            // CreateDirectory succeeds on a read-only folder that already
            // exists, so a real write is the only reliable check.
            var probe = Path.Combine(directory, ".write-test");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPortableDirectory(string directory)
    {
        try
        {
            var executableDirectory = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var parent = Path.GetDirectoryName(
                Path.GetFullPath(directory).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar));

            return string.Equals(parent, executableDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
