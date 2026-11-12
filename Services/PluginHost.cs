using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BagaGame.PluginApi;

namespace BagaGame.Services;

/// <summary>
/// Loads plugins from <c>%LOCALAPPDATA%\BagaGame\Plugins</c>. A plugin can be
/// a single DLL in that folder or a folder with a <c>plugin.json</c> manifest
/// and its dependencies. The host owns the tab list, builds navigation items
/// and keeps a broken plugin from taking the launcher down.
/// </summary>
public sealed class PluginHost : IDisposable
{
    private const string ManifestName = "plugin.json";

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly string _dataDirectory;
    private readonly string _pluginsDirectory;
    private readonly Dispatcher _dispatcher;
    private readonly List<LoadedPlugin> _plugins = new();
    private readonly Dictionary<string, HostedTab> _tabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pluginIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _pluginDirectories = new();

    private bool _loaded;
    private bool _disposed;

    public PluginHost(string dataDirectory, Dispatcher dispatcher)
    {
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _pluginsDirectory = Path.Combine(_dataDirectory, "Plugins");
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        Directory.CreateDirectory(_pluginsDirectory);

        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        Loc.Changed += OnLanguageChanged;
    }

    /// <summary>Absolute path of the folder the user drops plugins into.</summary>
    public string PluginsDirectory => _pluginsDirectory;

    public IReadOnlyList<HostedTab> Tabs =>
        _tabs.Values.OrderBy(tab => tab.Order).ThenBy(tab => tab.Title).ToList();

    public bool HasPlugins => _tabs.Count > 0;

    public event Action<HostedTab>? TabAdded;

    public event Action<string>? TabRemoved;

    public event Action<HostedTab>? TabUpdated;

    /// <summary>Raised on the UI thread after the launcher language changes.</summary>
    public event EventHandler? LanguageChanged;

    /// <summary>
    /// Discovers and activates every plugin. Call on the UI thread, after the
    /// main window is visible, so a slow plugin does not delay startup.
    /// </summary>
    public void LoadAll()
    {
        if (_loaded || _disposed)
        {
            return;
        }

        _loaded = true;
        Directory.CreateDirectory(_pluginsDirectory);

        foreach (var descriptor in Discover())
        {
            try
            {
                LoadPlugin(descriptor);
            }
            catch (Exception exception)
            {
                Log($"Plugin {descriptor.AssemblyPath} failed: {exception}");
            }
        }

        Log($"Plugin scan finished: {_plugins.Count} plugin(s), {_tabs.Count} tab(s).");
    }

    private IEnumerable<PluginDescriptor> Discover()
    {
        // A plugin folder with a manifest is the recommended layout: the
        // manifest names the entry assembly and documents the plugin.
        foreach (var folder in Directory.EnumerateDirectories(_pluginsDirectory))
        {
            _pluginDirectories.Add(Path.GetFullPath(folder));

            var manifestPath = Path.Combine(folder, ManifestName);
            if (File.Exists(manifestPath))
            {
                PluginManifest? manifest = null;
                try
                {
                    manifest = JsonSerializer.Deserialize<PluginManifest>(
                        File.ReadAllText(manifestPath),
                        ManifestJson);
                }
                catch (Exception exception)
                {
                    Log($"Manifest {manifestPath} is invalid: {exception.Message}");
                    continue;
                }

                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Assembly))
                {
                    Log($"Manifest {manifestPath} has no assembly entry.");
                    continue;
                }

                yield return new PluginDescriptor(
                    Path.Combine(folder, manifest.Assembly),
                    folder,
                    manifest);
                continue;
            }

            // No manifest: use the first DLL that contains a plugin type.
            foreach (var dll in Directory.EnumerateFiles(folder, "*.dll", SearchOption.TopDirectoryOnly))
            {
                yield return new PluginDescriptor(dll, folder, null);
            }
        }

        // Single-file plugins dropped directly into the Plugins folder.
        foreach (var dll in Directory.EnumerateFiles(_pluginsDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            yield return new PluginDescriptor(dll, _pluginsDirectory, null);
        }
    }

    private void LoadPlugin(PluginDescriptor descriptor)
    {
        var assemblyPath = Path.GetFullPath(descriptor.AssemblyPath);
        if (!File.Exists(assemblyPath))
        {
            Log($"Plugin assembly not found: {assemblyPath}");
            return;
        }

        var assembly = Assembly.LoadFrom(assemblyPath);

        foreach (var type in GetLoadableTypes(assembly))
        {
            if (type.IsAbstract ||
                type.IsInterface ||
                !type.IsClass ||
                !typeof(IBagaGamePlugin).IsAssignableFrom(type) ||
                type.GetConstructor(Type.EmptyTypes) is null)
            {
                continue;
            }

            IBagaGamePlugin plugin;
            try
            {
                plugin = (IBagaGamePlugin)Activator.CreateInstance(type)!;
            }
            catch (Exception exception)
            {
                Log($"Could not create {type.FullName} from {assemblyPath}: {exception}");
                continue;
            }

            if (plugin.ApiVersion != PluginApiInfo.Version)
            {
                Log($"Plugin {plugin.Id} needs API {plugin.ApiVersion}, host has {PluginApiInfo.Version}; skipped.");
                continue;
            }

            if (!_pluginIds.Add(plugin.Id))
            {
                Log($"Duplicate plugin id {plugin.Id}; skipped.");
                continue;
            }

            try
            {
                var context = new PluginHostContext(this, plugin.Id, descriptor.Folder);
                plugin.Initialize(context);
                _plugins.Add(new LoadedPlugin(plugin, descriptor.Folder));
                Log($"Loaded {plugin.Name} {plugin.Version} ({plugin.Id}) from {descriptor.Folder}");
            }
            catch (Exception exception)
            {
                Log($"Plugin {plugin.Id} failed during Initialize: {exception}");
            }
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null)!;
        }
    }

    internal void AddTab(PluginTab definition, string pluginId, string pluginDirectory)
    {
        if (_disposed)
        {
            return;
        }

        void Apply()
        {
            if (_tabs.ContainsKey(definition.Id))
            {
                Log($"Duplicate tab id {definition.Id} from {pluginId}; ignored.");
                return;
            }

            var tab = new HostedTab(definition, pluginId, pluginDirectory);
            _tabs[definition.Id] = tab;
            TabAdded?.Invoke(tab);
        }

        RunOnUi(Apply);
    }

    internal void RemoveTab(string id)
    {
        if (_disposed || string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        RunOnUi(() =>
        {
            if (_tabs.Remove(id))
            {
                TabRemoved?.Invoke(id);
            }
        });
    }

    internal void UpdateTab(string id, string? title, ImageSource? icon)
    {
        if (_disposed || string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        RunOnUi(() =>
        {
            if (!_tabs.TryGetValue(id, out var tab))
            {
                return;
            }

            if (title is not null)
            {
                tab.Definition.Title = title;
            }

            if (icon is not null)
            {
                tab.Definition.Icon = icon;
            }

            TabUpdated?.Invoke(tab);
        });
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
        }
    }

    /// <summary>
    /// Resolves the stable API assembly and plugin dependencies. The host's
    /// already loaded assembly always wins, so a plugin cannot replace the
    /// launcher's own copy of the contract or JSON stack.
    /// </summary>
    private Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        if (_disposed)
        {
            return null;
        }

        string? simpleName;
        try
        {
            simpleName = new AssemblyName(args.Name).Name;
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(simpleName))
        {
            return null;
        }

        if (string.Equals(simpleName, "BagaGame.PluginApi", StringComparison.OrdinalIgnoreCase))
        {
            return typeof(IBagaGamePlugin).Assembly;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
            {
                return assembly;
            }
        }

        foreach (var directory in _pluginDirectories)
        {
            var candidate = Path.Combine(directory, simpleName + ".dll");
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                return Assembly.LoadFrom(candidate);
            }
            catch (Exception exception)
            {
                Log($"Could not resolve {simpleName} from {candidate}: {exception.Message}");
            }
        }

        return null;
    }

    private void OnLanguageChanged() => LanguageChanged?.Invoke(this, EventArgs.Empty);

    public void Log(string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(_dataDirectory, "plugin.log"), line);
        }
        catch
        {
            // Never let diagnostics break a plugin or the launcher.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve;
        Loc.Changed -= OnLanguageChanged;

        foreach (var loaded in _plugins)
        {
            try
            {
                loaded.Plugin.Shutdown();
            }
            catch (Exception exception)
            {
                Log($"Plugin {loaded.Plugin.Id} failed during Shutdown: {exception}");
            }
        }

        _plugins.Clear();
        _tabs.Clear();
    }

    private sealed record PluginDescriptor(
        string AssemblyPath,
        string Folder,
        PluginManifest? Manifest);

    private sealed class PluginManifest
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Version { get; set; }

        public int ApiVersion { get; set; }

        public string? Assembly { get; set; }
    }

    private sealed record LoadedPlugin(IBagaGamePlugin Plugin, string Folder);

    /// <summary>One tab as the launcher sees it: metadata plus cached content.</summary>
    public sealed class HostedTab
    {
        private FrameworkElement? _content;
        private string? _contentError;

        internal HostedTab(PluginTab definition, string pluginId, string pluginDirectory)
        {
            Definition = definition;
            PluginId = pluginId;
            PluginDirectory = pluginDirectory;
        }

        public PluginTab Definition { get; }

        public string Id => Definition.Id;

        public string Title => Definition.Title;

        public ImageSource? Icon => Definition.Icon;

        public int Order => Definition.Order;

        public string PluginId { get; }

        public string PluginDirectory { get; }

        /// <summary>Creates the page once, on the UI thread, and caches it.</summary>
        public FrameworkElement GetContent()
        {
            if (_content is not null)
            {
                return _content;
            }

            if (_contentError is not null)
            {
                return BuildError(_contentError);
            }

            try
            {
                _content = Definition.CreateContent();
                if (_content is null)
                {
                    throw new InvalidOperationException("CreateContent returned null.");
                }

                return _content;
            }
            catch (Exception exception)
            {
                _contentError = $"Не удалось открыть вкладку плагина {PluginId}: {exception.Message}";
                return BuildError(_contentError);
            }
        }

        private static FrameworkElement BuildError(string text)
        {
            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(36) };
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                Foreground = Boom,
            });
            return panel;
        }

        private static readonly Brush Boom = CreateBoom();

        private static Brush CreateBoom()
        {
            var brush = new SolidColorBrush(Color.FromRgb(0xC8, 0x5A, 0x5A));
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>Host object handed to one plugin instance.</summary>
    private sealed class PluginHostContext : IPluginHost
    {
        private readonly PluginHost _owner;

        public PluginHostContext(PluginHost owner, string pluginId, string pluginDirectory)
        {
            _owner = owner;
            PluginId = pluginId;
            PluginDirectory = pluginDirectory;
        }

        public string PluginId { get; }

        public int ApiVersion => PluginApiInfo.Version;

        public string DataDirectory => _owner._dataDirectory;

        public string PluginDirectory { get; }

        public Dispatcher Dispatcher => _owner._dispatcher;

        public string Language => Loc.Current == AppLanguage.English ? "en" : "ru";

        public IPluginUi Ui { get; } = new PluginUi();

        public event EventHandler? LanguageChanged
        {
            add => _owner.LanguageChanged += value;
            remove => _owner.LanguageChanged -= value;
        }

        public void AddTab(PluginTab tab) => _owner.AddTab(tab, PluginId, PluginDirectory);

        public void RemoveTab(string id) => _owner.RemoveTab(id);

        public void UpdateTab(string id, string? title = null, ImageSource? icon = null)
            => _owner.UpdateTab(id, title, icon);

        public void Log(string message) => _owner.Log($"[{PluginId}] {message}");
    }
}
