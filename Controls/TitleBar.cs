using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BagaGame.Controls;

/// <summary>
/// Draggable caption strip with the three window buttons. Replaces
/// <c>Wpf.Ui.Controls.TitleBar</c>; the template lives in Themes/Controls.xaml.
/// </summary>
public class TitleBar : Control
{
    static TitleBar()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(TitleBar), new FrameworkPropertyMetadata(typeof(TitleBar)));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        Hook("PART_DragArea", OnDragAreaMouseDown);
        Hook("PART_Minimize", (_, _) => SetState(WindowState.Minimized));
        Hook("PART_Maximize", (_, _) => ToggleMaximize());
        Hook("PART_Close", (_, _) => WindowOf()?.Close());

        if (WindowOf() is { } window)
        {
            window.StateChanged -= OnWindowStateChanged;
            window.StateChanged += OnWindowStateChanged;
        }

        UpdateMaximizeGlyph();
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

        // A maximized window cannot be dragged, so restore it first and let the
        // user continue moving it from the cursor position.
        if (window.WindowState == WindowState.Maximized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.DragMove();
    }

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
