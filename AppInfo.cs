using System.Reflection;

namespace BagaGame;

/// <summary>
/// Strings shown in "About". Derived from the assembly so a version bump in the
/// project file cannot leave the window lying.
/// </summary>
public static class AppInfo
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version is { } version
            ? $"Версия {version.Major}.{version.Minor}.{version.Build}"
            : "Версия 0.0.0";

    public static string Runtime => ".NET Framework 4.7.2 · WPF";

    public static string Summary => $"{Version} · {Runtime}";
}
