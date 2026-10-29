namespace BagaGame.Services;

/// <summary>
/// System-tray presence used while a game is running. The launcher window is
/// hidden (not closed), so the playtime tracker keeps counting; the tray icon
/// brings the window back or quits the application.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ContextMenuStrip _menu;
    private bool _disposed;

    public TrayIcon(string tooltip, Action openWindow, Action exitApplication)
    {
        _menu = new System.Windows.Forms.ContextMenuStrip();
        _menu.Items.Add("Открыть BagaGame", null, (_, _) => openWindow());
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _menu.Items.Add("Выйти", null, (_, _) => exitApplication());

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadApplicationIcon(),
            Text = Shorten(tooltip),
            ContextMenuStrip = _menu,
            Visible = false,
        };

        _icon.DoubleClick += (_, _) => openWindow();
    }

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
            _icon.ShowBalloonTip(3000, title, text, System.Windows.Forms.ToolTipIcon.Info);
        }
        catch
        {
            // Notifications may be switched off; the icon is still there.
        }
    }

    private static System.Drawing.Icon LoadApplicationIcon()
    {
        try
        {
            var executable = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(executable))
            {
                var extracted = System.Drawing.Icon.ExtractAssociatedIcon(executable);
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

        return System.Drawing.SystemIcons.Application;
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
}