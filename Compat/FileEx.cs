using System.IO;

namespace BagaGame.Compat;

/// <summary>
/// The handful of file APIs that .NET Framework 4.7.2 lacks. Kept in one place
/// so the call sites stay readable and the fallbacks are easy to audit.
/// </summary>
internal static class FileEx
{
    /// <summary>
    /// Replaces <c>File.Move(source, destination, overwrite: true)</c>.
    /// File.Replace keeps the swap atomic; the copy fallback covers volumes
    /// where it is not supported.
    /// </summary>
    public static void MoveOverwrite(string source, string destination)
    {
        if (!File.Exists(destination))
        {
            File.Move(source, destination);
            return;
        }

        try
        {
            File.Replace(source, destination, destinationBackupFileName: null);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            File.Delete(destination);
            File.Move(source, destination);
        }
    }

    /// <summary>Replaces <c>File.WriteAllBytesAsync</c>.</summary>
    public static Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken)
        => Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.WriteAllBytes(path, bytes);
            },
            cancellationToken);
}
