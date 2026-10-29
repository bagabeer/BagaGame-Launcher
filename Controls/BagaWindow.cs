using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using BagaGame.Services;

namespace BagaGame.Controls;

/// <summary>
/// Borderless window with a custom title bar, Mica backdrop and native resize
/// behaviour. Replaces <c>Wpf.Ui.Controls.FluentWindow</c> without pulling in a
/// UI framework: the only platform work left is a few DWM attributes and the
/// taskbar-aware maximize bounds.
/// </summary>
public class BagaWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNearest = 0x00000002;

    public BagaWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Background = WindowEffects.WindowBackground();

        WindowChrome.SetWindowChrome(
            this,
            new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        WindowEffects.ApplyBackdrop(this);
        WindowEffects.SetDarkTitleBar(this, ThemeService.Current == AppTheme.Dark);

        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
        }
    }

    /// <summary>Re-applies the title bar colour after a theme switch.</summary>
    public void RefreshTheme()
        => WindowEffects.SetDarkTitleBar(this, ThemeService.Current == AppTheme.Dark);

    private static IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        // A borderless window maximizes over the taskbar by default. Clamp the
        // maximized bounds to the monitor work area instead.
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return IntPtr.Zero;
        }

        var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var work = info.WorkArea;
        var screen = info.Monitor;

        bounds.MaxPosition.X = Math.Abs(work.Left - screen.Left);
        bounds.MaxPosition.Y = Math.Abs(work.Top - screen.Top);
        bounds.MaxSize.X = Math.Abs(work.Right - work.Left);
        bounds.MaxSize.Y = Math.Abs(work.Bottom - work.Top);
        bounds.MaxTrackSize.X = bounds.MaxSize.X;
        bounds.MaxTrackSize.Y = bounds.MaxSize.Y;

        Marshal.StructureToPtr(bounds, lParam, fDeleteOld: true);
        handled = true;

        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
