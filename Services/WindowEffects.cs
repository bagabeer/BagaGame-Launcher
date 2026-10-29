using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace BagaGame.Services;

/// <summary>
/// Thin wrapper over the DWM attributes the launcher needs: the Mica backdrop,
/// rounded corners and a dark title bar. Everything degrades silently on
/// Windows 10, where those attributes do not exist.
/// </summary>
internal static class WindowEffects
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    private const int CornerRound = 2;
    private const int BackdropMica = 2;

    /// <summary>Mica and rounded corners are Windows 11 (build 22000) features.</summary>
    public static bool SupportsMica => Environment.OSVersion.Version.Build >= 22000;

    public static void ApplyBackdrop(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !SupportsMica)
        {
            return;
        }

        var backdrop = BackdropMica;
        DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));

        var corner = CornerRound;
        DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
    }

    public static void SetDarkTitleBar(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var value = dark ? 1 : 0;
        DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
    }

    /// <summary>
    /// A borderless window with a custom title bar still needs an opaque brush
    /// on Windows 10, otherwise the desktop shows through the client area.
    /// </summary>
    public static Brush WindowBackground()
        => SupportsMica
            ? Brushes.Transparent
            : new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int size);
}
