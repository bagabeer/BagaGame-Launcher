using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BagaGame.PluginApi;

/// <summary>
/// Stable resource keys of the launcher theme. Plugin XAML can use them with
/// <c>{DynamicResource ...}</c>; the expressions update when the user switches
/// between the dark and light theme.
/// </summary>
public static class PluginThemeKeys
{
    public const string Background = "WindowTintBrush";
    public const string Card = "CardFillBrush";
    public const string CardStroke = "CardStrokeBrush";
    public const string Divider = "DividerBrush";
    public const string TileFill = "TileFillBrush";
    public const string TextPrimary = "TextPrimaryBrush";
    public const string TextSecondary = "TextSecondaryBrush";
    public const string TextTertiary = "TextTertiaryBrush";
    public const string Accent = "AccentBrush";
    public const string AccentForeground = "AccentForegroundBrush";
    public const string AccentSoft = "AccentSoftBrush";
    public const string AccentStroke = "AccentStrokeBrush";
}

/// <summary>
/// Small UI kit that builds controls in the launcher's visual language. Every
/// brush is a dynamic resource, so plugin pages follow the dark and light
/// theme without extra code.
/// </summary>
public interface IPluginUi
{
    /// <summary>Root page panel with the launcher's standard margins.</summary>
    StackPanel Page();

    /// <summary>Large page heading, like the library or settings title.</summary>
    TextBlock PageTitle(string text);

    /// <summary>Muted line under the page heading.</summary>
    TextBlock PageSubtitle(string text);

    /// <summary>Small uppercase-style section label.</summary>
    TextBlock SectionTitle(string text);

    /// <summary>Regular body text, wrapped.</summary>
    TextBlock Body(string text);

    /// <summary>Rounded card with the launcher's card fill and stroke.</summary>
    Border Card(UIElement content);

    /// <summary>Accent (green) action button.</summary>
    Button PrimaryButton(string text);

    /// <summary>Quiet button with a card stroke.</summary>
    Button SecondaryButton(string text);
}

public sealed class PluginUi : IPluginUi
{
    private static readonly FontFamily DisplayFont =
        new("Segoe UI Variable Display, Segoe UI");

    public StackPanel Page()
        => new() { Margin = new Thickness(36, 26, 36, 26) };

    public TextBlock PageTitle(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = DisplayFont,
            FontSize = 30,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };

        block.SetResourceReference(TextBlock.ForegroundProperty, PluginThemeKeys.TextPrimary);
        return block;
    }

    public TextBlock PageSubtitle(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            Margin = new Thickness(1, 6, 0, 0),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };

        block.SetResourceReference(TextBlock.ForegroundProperty, PluginThemeKeys.TextSecondary);
        return block;
    }

    public TextBlock SectionTitle(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            Margin = new Thickness(2, 24, 0, 10),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
        };

        block.SetResourceReference(TextBlock.ForegroundProperty, PluginThemeKeys.TextTertiary);
        return block;
    }

    public TextBlock Body(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
        };

        block.SetResourceReference(TextBlock.ForegroundProperty, PluginThemeKeys.TextSecondary);
        return block;
    }

    public Border Card(UIElement content)
    {
        var border = new Border
        {
            Padding = new Thickness(20),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 10),
            Child = content,
        };

        border.SetResourceReference(Border.BackgroundProperty, PluginThemeKeys.Card);
        border.SetResourceReference(Border.BorderBrushProperty, PluginThemeKeys.CardStroke);
        return border;
    }

    public Button PrimaryButton(string text) => BuildButton(text, primary: true);

    public Button SecondaryButton(string text) => BuildButton(text, primary: false);

    private static Button BuildButton(string text, bool primary)
    {
        var button = new Button
        {
            Content = text,
            Height = 34,
            Padding = new Thickness(14, 0, 14, 0),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand,
            BorderThickness = new Thickness(primary ? 0 : 1),
        };

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetValue(
            Border.BackgroundProperty,
            new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(
            Border.BorderBrushProperty,
            new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(
            Border.BorderThicknessProperty,
            new TemplateBindingExtension(Control.BorderThicknessProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(
            ContentPresenter.HorizontalAlignmentProperty,
            HorizontalAlignment.Center);
        presenter.SetValue(
            ContentPresenter.VerticalAlignmentProperty,
            VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true,
        };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.86));
        template.Triggers.Add(hover);
        button.Template = template;

        if (primary)
        {
            button.SetResourceReference(Control.BackgroundProperty, PluginThemeKeys.Accent);
            button.SetResourceReference(Control.ForegroundProperty, PluginThemeKeys.AccentForeground);
        }
        else
        {
            button.SetResourceReference(Control.BackgroundProperty, PluginThemeKeys.TileFill);
            button.SetResourceReference(Control.ForegroundProperty, PluginThemeKeys.TextPrimary);
            button.SetResourceReference(Control.BorderBrushProperty, PluginThemeKeys.CardStroke);
        }

        return button;
    }
}
