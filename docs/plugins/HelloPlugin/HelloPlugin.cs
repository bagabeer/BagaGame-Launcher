using System;
using System.Windows;
using System.Windows.Controls;
using BagaGame.PluginApi;

namespace HelloPlugin;

/// <summary>
/// Minimal plugin: one navigation tab and a WPF page built in code.
/// Copy HelloPlugin.dll and plugin.json into
/// %LOCALAPPDATA%\BagaGame\Plugins\HelloPlugin\ and restart the launcher.
/// </summary>
public sealed class HelloPlugin : IBagaGamePlugin
{
    private IPluginHost? _host;
    private IPluginUi? _ui;

    public string Id => "sample.hello";

    public string Name => "Hello Plugin";

    public Version Version => new(1, 0, 0);

    public int ApiVersion => PluginApiInfo.Version;

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _ui = host.Ui;

        host.AddTab(new PluginTab(
            id: "hello",
            title: "Hello",
            createContent: CreatePage,
            order: 10));
    }

    private FrameworkElement CreatePage()
    {
        var ui = _ui ?? new PluginUi();

        var page = ui.Page();
        page.Children.Add(ui.PageTitle("Hello from a plugin"));
        page.Children.Add(ui.PageSubtitle(
            "This tab is built by the plugin. Data folder: "
            + (_host?.DataDirectory ?? "unknown")));

        page.Children.Add(ui.SectionTitle("DEMO CARD"));

        var cardContent = new StackPanel();
        cardContent.Children.Add(ui.Body(
            "The card, headings and buttons use the launcher theme, "
            + "so a plugin looks native in both the dark and light theme."));

        var button = ui.PrimaryButton("Click me");
        button.Margin = new Thickness(0, 14, 0, 0);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Click += (_, _) =>
            MessageBox.Show("The plugin UI works.", "Hello Plugin");

        cardContent.Children.Add(button);
        page.Children.Add(ui.Card(cardContent));

        return page;
    }

    public void Shutdown()
    {
        _host = null;
        _ui = null;
    }
}
