using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BagaGame.Controls;
using BagaGame.Models;
using BagaGame.Services;

namespace BagaGame.Views;

/// <summary>
/// Emulator picker split by family. Only families the game really uses get a
/// tab: Steam (local loaders and Goldberg), Epic/Nemirtingas when an EOS SDK
/// exists and GOG/UniverseLAN when Galaxy is present.
/// </summary>
public partial class EmulatorsWindow : BagaWindow
{
    private enum Tab
    {
        Local,
        Goldberg,
        Epic,
        Gog,
    }

    private readonly Game _game;
    private readonly int _appId;
    private readonly IReadOnlyList<Game> _library;
    private readonly ObservableCollection<EmulatorOption> _local = new();
    private readonly ObservableCollection<EmulatorOption> _goldberg = new();
    private readonly ObservableCollection<EmulatorOption> _epic = new();
    private readonly ObservableCollection<EmulatorOption> _gog = new();
    private readonly ObservableCollection<DownloadRelease> _releases = new();
    private readonly ObservableCollection<DownloadRelease> _tabReleases = new();
    private readonly CancellationTokenSource _lifetime = new();
    private List<EmulatorOption> _scanned = new();
    private Tab _tab = Tab.Local;
    private bool _busy;
    private bool _releasesLoaded;
    private bool _switchingTab;
    private bool _hasEpic;
    private bool _hasGog;

    public EmulatorsWindow(Game game, int appId, IEnumerable<Game> library)
    {
        InitializeComponent();

        _game = game;
        _appId = appId;
        _library = library?.ToList() ?? new List<Game>();

        GameTitle.Text = game.Title;
        ReleasesList.ItemsSource = _tabReleases;

        Loaded += async (_, _) =>
        {
            BuildTabs();
            RefreshOptions();
            await LoadReleasesAsync();
            await ScanLocalAsync();
        };

        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    // ------------------------------------------------------------------
    // Tabs
    // ------------------------------------------------------------------

    private void BuildTabs()
    {
        var hasSteam = EmulatorSwapService.FindTarget(_game, EmulatorFamily.Steam) is not null;
        _hasEpic = EmulatorSwapService.FindTarget(_game, EmulatorFamily.Epic) is not null;
        _hasGog = EmulatorSwapService.FindTarget(_game, EmulatorFamily.Gog) is not null;

        if (hasSteam)
        {
            AddTab(Tab.Local, "Loc.Emulators.Tab.Local");
            AddTab(Tab.Goldberg, "Loc.Emulators.Tab.Goldberg");
        }

        if (_hasEpic)
        {
            AddTab(Tab.Epic, "Loc.Emulators.Tab.Epic");
        }

        if (_hasGog)
        {
            AddTab(Tab.Gog, "Loc.Emulators.Tab.Gog");
        }

        var targets = new[] { EmulatorFamily.Steam, EmulatorFamily.Epic, EmulatorFamily.Gog }
            .Select(family => EmulatorSwapService.FindTarget(_game, family))
            .Where(target => target is not null)
            .Select(target => target!.Directory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        TargetPath.Text = targets.Count == 0
            ? Loc.T("Loc.Emulators.NoTarget")
            : string.Join(" · ", targets);

        if (TabsPanel.Children.Count == 0)
        {
            StatusText.Text = Loc.T("Loc.Emulators.NoTarget");
            ApplyButton.IsEnabled = false;
            RestoreButton.IsEnabled = false;
            DownloadsPanel.Visibility = Visibility.Collapsed;
            DownloadsColumn.Width = new GridLength(0);
            GapColumn.Width = new GridLength(0);
            return;
        }

        _switchingTab = true;
        ((RadioButton)TabsPanel.Children[0]).IsChecked = true;
        _switchingTab = false;
        SelectTab((Tab)((RadioButton)TabsPanel.Children[0]).Tag);
    }

    private void AddTab(Tab tab, string titleKey)
    {
        var button = new RadioButton
        {
            Content = Loc.T(titleKey),
            Style = (Style)FindResource("TabChip"),
            GroupName = "EmulatorTabs",
            Tag = tab,
        };

        button.Checked += (_, _) =>
        {
            if (!_switchingTab)
            {
                SelectTab((Tab)button.Tag);
            }
        };

        TabsPanel.Children.Add(button);
    }

    private void SelectTab(Tab tab)
    {
        _tab = tab;

        OptionsList.ItemsSource = tab switch
        {
            Tab.Goldberg => _goldberg,
            Tab.Epic => _epic,
            Tab.Gog => _gog,
            _ => _local,
        };

        var downloads = tab is Tab.Goldberg or Tab.Epic or Tab.Gog;
        DownloadsPanel.Visibility = downloads ? Visibility.Visible : Visibility.Collapsed;
        DownloadsColumn.Width = downloads ? new GridLength(300) : new GridLength(0);
        GapColumn.Width = downloads ? new GridLength(16) : new GridLength(0);

        DownloadsTitle.Text = tab switch
        {
            Tab.Epic => Loc.T("Loc.Emulators.DownloadNemirtingas"),
            Tab.Gog => Loc.T("Loc.Emulators.DownloadUniverseLAN"),
            _ => Loc.T("Loc.Emulators.DownloadGoldberg"),
        };

        if (downloads)
        {
            ApplyReleaseFilter(tab switch
            {
                Tab.Epic => EmulatorFamily.Epic,
                Tab.Gog => EmulatorFamily.Gog,
                _ => EmulatorFamily.Steam,
            });
        }
        else
        {
            _tabReleases.Clear();
        }

        StatusText.Text = tab switch
        {
            Tab.Local => Loc.T("Loc.Emulators.LocalHint"),
            Tab.Gog when EmulatorSwapService.FindTarget(_game, EmulatorFamily.Gog) is { } gog &&
                          !File.Exists(gog.FilePath) => Loc.T("Loc.Emulators.GogHint"),
            _ => string.Empty,
        };

        UpdateApplyState();
        UpdateRestoreState();
        UpdateDownloadState();
    }

    // ------------------------------------------------------------------
    // Data
    // ------------------------------------------------------------------

    private void RefreshOptions()
    {
        var own = EmulatorSwapService.GetOptions(_game);
        var all = new List<EmulatorOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(EmulatorOption option)
        {
            if (string.IsNullOrWhiteSpace(option.Path) || !seen.Add(option.Path!))
            {
                return;
            }

            all.Add(option);
        }

        foreach (var option in own)
        {
            Add(option);
        }

        foreach (var option in _scanned)
        {
            Add(option);
        }

        Fill(_local, all.Where(option =>
            option.Family == EmulatorFamily.Steam && !option.IsDownloaded));
        Fill(_goldberg, all.Where(option =>
            option.Family == EmulatorFamily.Steam && option.IsDownloaded));
        Fill(_epic, all.Where(option => option.Family == EmulatorFamily.Epic));
        Fill(_gog, all.Where(option => option.Family == EmulatorFamily.Gog));

        UpdateApplyState();
        UpdateRestoreState();
    }

    private static void Fill(ObservableCollection<EmulatorOption> target, IEnumerable<EmulatorOption> source)
    {
        target.Clear();
        foreach (var option in source
                     .OrderByDescending(item => item.IsCurrent)
                     .ThenByDescending(item => item.IsBackup)
                     .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            target.Add(option);
        }
    }

    private async Task ScanLocalAsync()
    {
        try
        {
            var scanned = await Task.Run(
                () => EmulatorSwapService.ScanLibrary(_library, _game),
                _lifetime.Token);

            _scanned = scanned.ToList();
            RefreshOptions();
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
        catch
        {
            // A broken library only loses the extra candidates.
        }
    }

    private async Task LoadReleasesAsync(bool force = false)
    {
        if (_busy || (_releasesLoaded && !force))
        {
            return;
        }

        StatusText.Text = Loc.T("Loc.Emulators.Loading");

        try
        {
            var releases = await EmulatorDownloadService
                .GetReleasesAsync(null, _lifetime.Token, force)
                .ConfigureAwait(true);

            _releases.Clear();
            foreach (var release in releases)
            {
                _releases.Add(release);
            }

            _releasesLoaded = _releases.Count > 0;
            if (_tab is Tab.Goldberg or Tab.Epic or Tab.Gog)
            {
                ApplyReleaseFilter(_tab switch
                {
                    Tab.Epic => EmulatorFamily.Epic,
                    Tab.Gog => EmulatorFamily.Gog,
                    _ => EmulatorFamily.Steam,
                });
            }

            if (_releases.Count == 0)
            {
                StatusText.Text = Loc.T("Loc.Emulators.NoReleases");
            }
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
        catch
        {
            StatusText.Text = Loc.T("Loc.Emulators.LoadError");
        }
    }

    private void ApplyReleaseFilter(EmulatorFamily family)
    {
        var selected = (ReleasesList.SelectedItem as DownloadRelease)?.Tag;

        _tabReleases.Clear();
        foreach (var release in _releases.Where(item => item.Family == family))
        {
            _tabReleases.Add(release);
        }

        if (selected is not null)
        {
            ReleasesList.SelectedItem = _tabReleases.FirstOrDefault(item => item.Tag == selected);
        }

        if (ReleasesList.SelectedItem is null && _tabReleases.Count > 0)
        {
            ReleasesList.SelectedIndex = 0;
        }

        UpdateDownloadState();
    }

    // ------------------------------------------------------------------
    // Actions
    // ------------------------------------------------------------------

    private void OptionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateApplyState();

    private void ReleasesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateDownloadState();

    private void UpdateApplyState()
    {
        ApplyButton.IsEnabled =
            !_busy &&
            OptionsList.SelectedItem is EmulatorOption option &&
            !option.IsCurrent &&
            !string.IsNullOrWhiteSpace(option.Path);
    }

    private void UpdateDownloadState()
    {
        var release = ReleasesList.SelectedItem as DownloadRelease;
        var cached = release is not null && EmulatorDownloadService.IsCached(release);

        DownloadButton.IsEnabled = !_busy && release is not null && !cached;
        DownloadButton.Content = cached
            ? Loc.T("Loc.Emulators.AlreadyDownloaded")
            : Loc.T("Loc.Emulators.Download");
    }

    private void UpdateRestoreState()
    {
        var family = CurrentFamily();
        var canRestore = family is not null &&
                         EmulatorSwapService.HasBackup(_game, family.Value) &&
                         !EmulatorSwapService.IsOriginalActive(_game, family.Value);

        RestoreButton.IsEnabled = !_busy && canRestore;
        RestoreButton.ToolTip = canRestore
            ? null
            : Loc.T("Loc.Emulators.RestoreHint");
    }

    private EmulatorFamily? CurrentFamily() => _tab switch
    {
        Tab.Local or Tab.Goldberg => EmulatorFamily.Steam,
        Tab.Epic => EmulatorFamily.Epic,
        Tab.Gog => EmulatorFamily.Gog,
        _ => null,
    };

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (OptionsList.SelectedItem is not EmulatorOption option)
        {
            return;
        }

        try
        {
            EmulatorSwapService.Apply(_game, _appId, option);
            StatusText.Text = Loc.Format("Loc.Emulators.Applied", option.DisplayName);
            RefreshOptions();
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.Format("Loc.Emulators.Error", ex.Message);
        }
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        var family = CurrentFamily();
        if (family is null)
        {
            return;
        }

        try
        {
            StatusText.Text = EmulatorSwapService.Restore(_game, family.Value)
                ? Loc.T("Loc.Emulators.Restored")
                : Loc.T("Loc.Emulators.NoBackup");
            RefreshOptions();
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.Format("Loc.Emulators.Error", ex.Message);
        }
    }

    private void DeleteOption_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EmulatorOption option)
        {
            return;
        }

        if (!EmulatorSwapService.DeleteDownloaded(option))
        {
            StatusText.Text = Loc.T("Loc.Emulators.DeleteFailed");
            return;
        }

        StatusText.Text = Loc.Format("Loc.Emulators.Deleted", option.DisplayName);
        RefreshOptions();
        UpdateDownloadState();
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (ReleasesList.SelectedItem is not DownloadRelease release)
        {
            StatusText.Text = Loc.T("Loc.Emulators.NoReleases");
            return;
        }

        SetBusy(true);
        StatusText.Text = Loc.Format("Loc.Emulators.Downloading", release.DisplayName, 0);

        var progress = new Progress<double>(percent =>
            StatusText.Text = Loc.Format(
                "Loc.Emulators.Downloading",
                release.DisplayName,
                (int)Math.Round(percent)));

        var stage = new Progress<string>(value =>
        {
            if (string.Equals(value, "extract", StringComparison.Ordinal))
            {
                StatusText.Text = Loc.Format("Loc.Emulators.Extracting", release.DisplayName);
            }
        });

        try
        {
            await EmulatorDownloadService
                .EnsureAsync(release, progress, stage, _lifetime.Token)
                .ConfigureAwait(true);

            StatusText.Text = Loc.Format("Loc.Emulators.Ready", release.DisplayName);
            RefreshOptions();
            UpdateDownloadState();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.Format("Loc.Emulators.Error", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        _releasesLoaded = false;
        await LoadReleasesAsync(true);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateApplyState();
        UpdateRestoreState();
        UpdateDownloadState();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
