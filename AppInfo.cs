using System.Reflection;

namespace BagaGame;

/// <summary>
/// Data shown in "About". The version comes from the assembly so a bump in the
/// project file cannot leave the window lying; the label itself is localized by
/// <see cref="ViewModels.MainViewModel.VersionSummary"/>.
/// </summary>
public static class AppInfo
{
    /// <summary>Parsed assembly version, used to compare against a release tag.</summary>
    public static Version Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public static string VersionNumber { get; } =
        Version.Build >= 0
            ? $"{Version.Major}.{Version.Minor}.{Version.Build}"
            : $"{Version.Major}.{Version.Minor}";

    public static string Runtime => ".NET Framework 4.7.2 · WPF";
}
