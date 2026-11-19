using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BagaGame.Services;

/// <summary>
/// System-tray presence used while a game is running or the window was closed
/// to the tray. The launcher window is hidden (not closed), so the playtime
/// tracker keeps counting; the tray icon brings the window back or quits.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _exitItem;
    private bool _disposed;

    public TrayIcon(Action openWindow, Action exitApplication)
    {
        _openItem = CreateItem();
        _openItem.Click += (_, _) => openWindow();

        _exitItem = CreateItem();
        _exitItem.Click += (_, _) => exitApplication();

        _menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Padding = new Padding(3),
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
            DropShadowEnabled = false,
        };
        _menu.Items.Add(_openItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_exitItem);

        // Native tool strips are square; shape the drop-down so it follows the
        // rounded cards used everywhere else in the launcher.
        _menu.Opened += (_, _) => QueueRoundedRegion();
        _menu.SizeChanged += (_, _) => ApplyRoundedRegion();

        _icon = new NotifyIcon
        {
            Icon = LoadApplicationIcon(),
            ContextMenuStrip = _menu,
            Visible = false,
        };

        _icon.DoubleClick += (_, _) => openWindow();

        // A single left click is what most people try first; without it the
        // icon looks dead even though the double-click handler works.
        _icon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                openWindow();
            }
        };

        ApplyTheme();
        RefreshTexts();
    }

    /// <summary>Re-reads the menu labels after a language switch.</summary>
    public void RefreshTexts()
    {
        _openItem.Text = Loc.T("Loc.Tray.Open");
        _exitItem.Text = Loc.T("Loc.Tray.Exit");
        _icon.Text = Shorten(Loc.T("Loc.Tray.Tooltip"));
    }

    /// <summary>Re-paints the native menu after a light/dark theme switch.</summary>
    public void RefreshTheme() => ApplyTheme();

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    /// <summary>Short toast explaining where the launcher went (best effort).</summary>
    public void ShowBalloon(string title, string text)
    {
        try
        {
            _icon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);
        }
        catch
        {
            // Notifications may be switched off; the icon is still there.
        }
    }

    private static ToolStripMenuItem CreateItem()
        => new()
        {
            AutoSize = false,
            Height = 30,
            Width = 168,
            Padding = new Padding(10, 0, 10, 0),
            TextAlign = ContentAlignment.MiddleLeft,
        };

    private void QueueRoundedRegion()
    {
        if (!_menu.IsHandleCreated || _menu.IsDisposed)
        {
            return;
        }

        try
        {
            _menu.BeginInvoke(new Action(ApplyRoundedRegion));
        }
        catch (InvalidOperationException)
        {
            // The drop-down was closed while the size change was queued.
        }
    }

    private void ApplyRoundedRegion()
    {
        if (!_menu.IsHandleCreated || _menu.Width <= 0 || _menu.Height <= 0)
        {
            return;
        }

        // SetWindowRgn clips every corner of the native drop-down; the managed
        // Region property is ignored by the tool strip when it paints its
        // shadow, which left the right side square.
        var width = _menu.ClientSize.Width > 0 ? _menu.ClientSize.Width : _menu.Width;
        var height = _menu.ClientSize.Height > 0 ? _menu.ClientSize.Height : _menu.Height;

        var handle = CreateRoundRectRgn(
            0,
            0,
            width + 1,
            height + 1,
            9,
            9);

        if (handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowRgn(_menu.Handle, handle, true);
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(
        int left,
        int top,
        int right,
        int bottom,
        int width,
        int height);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);

    private void ApplyTheme()
    {
        var dark = ThemeService.Current == AppTheme.Dark;

        var surface = dark
            ? Color.FromArgb(42, 42, 42)
            : Color.FromArgb(242, 242, 239);

        var border = dark
            ? Color.FromArgb(31, 255, 255, 255)
            : Color.FromArgb(20, 0, 0, 0);

        var text = dark
            ? Color.FromArgb(243, 243, 243)
            : Color.FromArgb(26, 26, 26);

        var hover = dark
            ? Color.FromArgb(38, 166, 242, 107)
            : Color.FromArgb(31, 63, 122, 22);

        var separator = dark
            ? Color.FromArgb(20, 255, 255, 255)
            : Color.FromArgb(18, 0, 0, 0);

        _menu.BackColor = surface;
        _menu.ForeColor = text;
        _menu.Renderer = new TrayMenuRenderer(surface, text, hover, separator);

        _openItem.ForeColor = text;
        _exitItem.ForeColor = text;
    }

    private static Icon LoadApplicationIcon()
    {
        try
        {
            var executable = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(executable))
            {
                var extracted = Icon.ExtractAssociatedIcon(executable);
                if (extracted is not null)
                {
                    return extracted;
                }
            }
        }
        catch
        {
            // Fall back to the generic application icon.
        }

        return SystemIcons.Application;
    }

    /// <summary>NotifyIcon.Text is limited to 63 characters.</summary>
    private static string Shorten(string value)
        => string.IsNullOrWhiteSpace(value) ? "BagaGame" : value.Length <= 63 ? value : value.Substring(0, 63);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }

    /// <summary>Dark/light menu matching the launcher surfaces.</summary>
    private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly Color _surface;
        private readonly Color _text;
        private readonly Color _hover;
        private readonly Color _separator;

        public TrayMenuRenderer(
            Color surface,
            Color text,
            Color hover,
            Color separator)
        {
            _surface = surface;
            _text = text;
            _hover = hover;
            _separator = separator;
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(_surface);
            var bounds = e.ToolStrip.ClientRectangle;
            e.Graphics.FillRectangle(brush, bounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            // The drop-down is clipped by a rounded region; a border line would
            // only add a light fringe around it.
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // No icon column.
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected)
            {
                return;
            }

            var maxWidth = Math.Min(e.Item.Width, e.ToolStrip.ClientSize.Width);
            var bounds = new Rectangle(3, 1, Math.Max(0, maxWidth - 6), e.Item.Height - 2);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            using var path = RoundedRect(bounds, 6);
            using var brush = new SolidBrush(_hover);
            e.Graphics.FillPath(brush, path);
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var diameter = radius * 2;

            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = _text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(_separator);
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
        }
    }
}
