using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BagaGame.Controls;

/// <summary>
/// Draggable caption strip with the three window buttons. Replaces
/// <c>Wpf.Ui.Controls.TitleBar</c>; the template lives in Themes/Controls.xaml.
/// </summary>
public class TitleBar : Control
{
    private bool _dragging;
    private Point _dragStartScreen;
    private double _dragStartLeft;
    private double _dragStartTop;
    private DpiScale _dragDpi = new(1, 1);
    private UIElement? _dragSurface;

    static TitleBar()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(TitleBar), new FrameworkPropertyMetadata(typeof(TitleBar)));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        Hook("PART_Minimize", (_, _) => SetState(WindowState.Minimized));
        Hook("PART_Maximize", (_, _) => ToggleMaximize());
        Hook("PART_Close", (_, _) => WindowOf()?.Close());
        HookDragArea();

        if (WindowOf() is { } window)
        {
            window.StateChanged -= OnWindowStateChanged;
            window.StateChanged += OnWindowStateChanged;
        }

        UpdateMaximizeGlyph();
    }

    private void HookDragArea()
    {
        if (GetTemplateChild("PART_DragArea") is not UIElement element)
        {
            return;
        }

        if (_dragSurface is not null)
        {
            _dragSurface.MouseLeftButtonDown -= OnDragAreaMouseDown;
            _dragSurface.MouseMove -= OnDragAreaMouseMove;
            _dragSurface.MouseLeftButtonUp -= OnDragAreaMouseUp;
            _dragSurface.LostMouseCapture -= OnDragLostCapture;
        }

        _dragSurface = element;
        element.MouseLeftButtonDown += OnDragAreaMouseDown;
        element.MouseMove += OnDragAreaMouseMove;
        element.MouseLeftButtonUp += OnDragAreaMouseUp;
        element.LostMouseCapture += OnDragLostCapture;
    }

    private void Hook(string partName, MouseButtonEventHandler handler)
    {
        if (GetTemplateChild(partName) is UIElement element)
        {
            element.MouseLeftButtonDown -= handler;
            element.MouseLeftButtonDown += handler;
        }
    }

    private void OnDragAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (WindowOf() is not { } window)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            var screen = PointToScreen(e.GetPosition(this));

            // A maximized window restores first; keep the cursor over the same
            // spot on the caption instead of jumping to the old normal bounds.
            if (window.WindowState == WindowState.Maximized)
            {
                var relative = e.GetPosition(window);
                var ratio = window.ActualWidth > 0 ? relative.X / window.ActualWidth : 0.5;

                window.WindowState = WindowState.Normal;

                var dpi = VisualTreeHelper.GetDpi(window);
                window.Left = screen.X / dpi.DpiScaleX - ratio * window.Width;
                window.Top = screen.Y / dpi.DpiScaleY - relative.Y;
            }

            _dragStartScreen = screen;
            _dragStartLeft = window.Left;
            _dragStartTop = window.Top;
            _dragDpi = VisualTreeHelper.GetDpi(window);
            _dragging = true;

            // A manual move keeps the window at its normal size. The native
            // DragMove enters the shell move loop, which snaps the window to a
            // screen edge (and resizes it) while crossing to another monitor.
            (sender as UIElement)?.CaptureMouse();
            e.Handled = true;
        }
        catch
        {
            _dragging = false;
        }
    }

    private void OnDragAreaMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (WindowOf() is not { } window)
        {
            _dragging = false;
            return;
        }

        try
        {
            var screen = PointToScreen(e.GetPosition(this));
            window.Left = _dragStartLeft + (screen.X - _dragStartScreen.X) / _dragDpi.DpiScaleX;
            window.Top = _dragStartTop + (screen.Y - _dragStartScreen.Y) / _dragDpi.DpiScaleY;
            e.Handled = true;
        }
        catch
        {
            _dragging = false;
        }
    }

    private void OnDragAreaMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        (sender as UIElement)?.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnDragLostCapture(object sender, MouseEventArgs e) => _dragging = false;

    private void ToggleMaximize()
    {
        if (WindowOf() is not { } window)
        {
            return;
        }

        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void SetState(WindowState state)
    {
        if (WindowOf() is { } window)
        {
            window.WindowState = state;
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateMaximizeGlyph();

    private void UpdateMaximizeGlyph()
    {
        if (GetTemplateChild("PART_MaximizeIcon") is TextBlock icon)
        {
            icon.Text = WindowOf()?.WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        }
    }

    private Window? WindowOf() => Window.GetWindow(this);
}
