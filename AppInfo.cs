using System.Reflection;

namespace BagaGame;

/// <summary>
/// Data shown in "About". The version comes from the assembly so a bump in the
/// project file cannot leave the window lying; the label itself is localized by
/// <see cref="ViewModels.MainViewModel.VersionSummary"/>.
/// </summary>
public static class AppInfo
{
    public static string VersionNumber { get; } =
        Assembly.GetExecutingAssembly().GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "0.0.0";

    public static string Runtime => ".NET Framework 4.7.2 · WPF";
}
