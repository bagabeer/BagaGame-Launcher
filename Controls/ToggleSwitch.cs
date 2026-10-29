using System.Windows.Controls.Primitives;

namespace BagaGame.Controls;

/// <summary>
/// Fluent toggle switch. <see cref="ToggleButton"/> already provides
/// <c>IsChecked</c>, <c>Checked</c> and <c>Unchecked</c>, so only the template
/// differs; the look lives in Themes/Controls.xaml.
/// </summary>
public class ToggleSwitch : ToggleButton;
