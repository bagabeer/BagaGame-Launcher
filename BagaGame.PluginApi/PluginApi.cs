using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace BagaGame.PluginApi;

/// <summary>Version of the plugin contract, independent of the launcher version.</summary>
public static class PluginApiInfo
{
    /// <summary>
    /// Breaking changes bump this number. A plugin declares the version it was
    /// built against and the host refuses anything it does not understand.
    /// </summary>
    public const int Version = 1;
}

/// <summary>
/// Entry point of a launcher plugin. One assembly may contain several plugins;
/// each type with a public parameterless constructor is activated separately.
/// </summary>
public interface IBagaGamePlugin
{
    /// <summary>Stable identifier, for example <c>com.example.hello</c>.</summary>
    string Id { get; }

    /// <summary>Human-readable name shown in diagnostics.</summary>
    string Name { get; }

    /// <summary>Plugin version, not the launcher version.</summary>
    Version Version { get; }

    /// <summary>Contract version the plugin was compiled against.</summary>
    int ApiVersion { get; }

    /// <summary>
    /// Called once on the UI thread. Register tabs here; do not block.
    /// </summary>
    void Initialize(IPluginHost host);

    /// <summary>Called when the launcher exits. Release timers and files here.</summary>
    void Shutdown();
}

/// <summary>Services the launcher exposes to a plugin.</summary>
public interface IPluginHost
{
    /// <summary>Contract version of the running launcher.</summary>
    int ApiVersion { get; }

    /// <summary>Portable data folder, for example <c>%LOCALAPPDATA%\BagaGame</c>.</summary>
    string DataDirectory { get; }

    /// <summary>Folder the plugin was loaded from, for its own assets and cache.</summary>
    string PluginDirectory { get; }

    /// <summary>UI dispatcher; use it for every WPF change from a background thread.</summary>
    Dispatcher Dispatcher { get; }

    /// <summary>Current interface language: <c>ru</c> or <c>en</c>.</summary>
    string Language { get; }

    /// <summary>Raised on the UI thread after the interface language changes.</summary>
    event EventHandler? LanguageChanged;

    /// <summary>Controls and brushes that match the launcher's visual style.</summary>
    IPluginUi Ui { get; }

    /// <summary>Adds a tab to the left navigation and a page to the main area.</summary>
    void AddTab(PluginTab tab);

    /// <summary>Removes a previously added tab.</summary>
    void RemoveTab(string id);

    /// <summary>Changes a tab title or icon after it was added.</summary>
    void UpdateTab(string id, string? title = null, ImageSource? icon = null);

    /// <summary>Writes a line to the launcher's plugin log.</summary>
    void Log(string message);
}

/// <summary>
/// One plugin tab. <see cref="CreateContent"/> is called lazily on the UI
/// thread the first time the user opens the tab, and the result is cached.
/// </summary>
public sealed class PluginTab
{
    public PluginTab(
        string id,
        string title,
        Func<FrameworkElement> createContent,
        ImageSource? icon = null,
        int order = 100)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("A tab id is required.", nameof(id));
        }

        Id = id;
        Title = title ?? string.Empty;
        CreateContent = createContent ?? throw new ArgumentNullException(nameof(createContent));
        Icon = icon;
        Order = order;
    }

    public string Id { get; }

    public string Title { get; set; }

    public ImageSource? Icon { get; set; }

    public int Order { get; }

    public Func<FrameworkElement> CreateContent { get; }
}
