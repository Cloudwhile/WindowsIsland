using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class SettingsOfficialPlugins : StackPanel, IDisposable
{
    private readonly PluginLibrary _library;
    private readonly Action<SettingsDetail> _open;
    private readonly TextBox _proxy = new() { MinWidth = 0 };
    private readonly IconActionButton _save = new(Symbol.Save, "PluginSaveProxy", "SavePluginProxy");
    private readonly IconActionButton _refresh = new(Symbol.Refresh, "PluginRefreshCatalog", "RefreshPluginCatalog");
    private readonly IconActionButton _cancel = new(Symbol.Cancel, "PluginCancel", "CancelOfficialPlugin") { Visibility = Visibility.Collapsed };
    private readonly StackPanel _plugins = new();
    private readonly TextBlock _status = LocalizedUI.Text("PluginCatalogEmpty", 12, secondary: true);
    private readonly ProgressRing _progress = new() { Width = 20, Height = 20, IsActive = false, Visibility = Visibility.Collapsed };
    private readonly SettingsEmptyState _empty = new(Symbol.AllApps, "PluginCatalogEmpty");
    private readonly InfoBar _notice = new() { IsOpen = true, IsClosable = false, Visibility = Visibility.Collapsed };
    private readonly List<(IconActionButton Button, SettingsRow Row)> _actions = [];
    private readonly List<PluginCatalogNotice> _notices = [];
    private PluginMetadata[] _installed = [];
    private PluginCatalogState? _rendered;
    private bool _renderedPrereleases;
    private string? _lastProxy;
    private CancellationTokenSource? _operation;
    private ContentDialog? _dialog;
    private bool _disposed;
    private string? _noticeKey;
    private InfoBar? _detailNotice;
    private IconActionButton? _detailCancel;
    private bool _refreshing;

    public SettingsOfficialPlugins(PluginLibrary library, Action<SettingsDetail> open)
    {
        _library = library;
        _open = open;
        var sourceNotice = LocalizedUI.Text("PluginDefaultSourceNotice", SettingsTokens.DescriptionTextSize, secondary: true);
        AutomationProperties.SetAutomationId(sourceNotice, "PluginDefaultSourceNotice");
        Children.Add(sourceNotice);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = SettingsTokens.ControlSpacing };
        toolbar.Children.Add(_progress);
        toolbar.Children.Add(_refresh);
        toolbar.Children.Add(_cancel);
        _proxy.Text = library.Catalog.State.GitHubProxy;
        LocalizedUI.Label(_proxy, "PluginGitHubProxy");
        LocalizedUI.Bind(_proxy, () => _proxy.PlaceholderText = Localization.Get("PluginProxyDirect"));
        AutomationProperties.SetAutomationId(_proxy, "PluginGitHubProxy");
        _status.TextWrapping = TextWrapping.Wrap;
        Children.Add(new SettingsSection("PluginOfficial", new SettingsRow(Symbol.World, "PluginOfficial", toolbar),
            SettingsContent.Wrap(_status), SettingsContent.Wrap(_notice)));
        AutomationProperties.SetAutomationId(_empty, "PluginCatalogEmptyState");
        Children.Add(_empty);
        Children.Add(_plugins);
        var advanced = new SettingsExpander(new SymbolIcon(Symbol.Setting), "PluginAdvanced", "PluginAdvancedSettings")
        { ExpandOnHeaderTap = true };
        advanced.Rows.Add(new SettingsRow(Symbol.Link, "PluginGitHubProxy", _save));
        advanced.Rows.Add(new SettingsContent(false, _proxy));
        Children.Add(advanced);
        _save.Click += async (_, _) => await RunAsync(async _ =>
        {
            await library.Catalog.ConfigureProxyAsync(_proxy.Text);
            Notice("PluginProxySaved", InfoBarSeverity.Success);
        });
        _refresh.Click += async (_, _) => await RunAsync(async token =>
        {
            _refreshing = true;
            Refresh();
            try { await library.Catalog.RefreshAsync(token); }
            finally { _refreshing = false; }
        });
        _cancel.Click += (_, _) => _operation?.Cancel();
        library.Catalog.Changed += OnChanged;
        LocalizedUI.Bind(this, () => { _rendered = null; Refresh(); if (_noticeKey is { } key) _notice.Message = Localization.Get(key); });
        Refresh();
    }

    private void OnChanged() => DispatcherQueue.TryEnqueue(() => { if (!_disposed) Refresh(); });

    public void Refresh()
    {
        if (_disposed) return;
        var state = _library.Catalog.State;
        var installedPlugins = _library.Manager.Snapshot().Select(plugin => plugin.Definition.Metadata)
            .OfType<PluginMetadata>().Where(metadata => metadata.External is not null).ToArray();
        if (_lastProxy != state.GitHubProxy && _operation is null) { _lastProxy = state.GitHubProxy; _proxy.Text = state.GitHubProxy; }
        var loading = state.Busy || _refreshing;
        _progress.IsActive = loading || _operation is not null;
        _progress.Visibility = _progress.IsActive ? Visibility.Visible : Visibility.Collapsed;
        _status.Text = Localization.Get(loading ? "PluginCatalogLoading" : state.Failure is { } failure ? PluginErrors.Key(failure)
            : state.Unavailable ? "PluginCatalogUnavailable" : state.AvailableSources.Length == 0 ? "PluginCatalogEmpty" : "PluginCatalogReady");
        if ((!ReferenceEquals(_rendered, state) || _renderedPrereleases != _library.Catalog.IncludePrereleases
            || !_installed.SequenceEqual(installedPlugins)) && _operation is null)
        {
            _rendered = state;
            _renderedPrereleases = _library.Catalog.IncludePrereleases;
            _installed = installedPlugins;
            _plugins.Children.Clear();
            _actions.Clear();
            _notices.Clear();
            var delisted = installedPlugins.Where(metadata => _library.Catalog.IsDelisted(metadata.External!.Manifest)).ToArray();
            var developers = state.AvailableSources.Select(index => index.Source.DeveloperId)
                .Concat(delisted.Select(metadata => metadata.External!.Manifest.DeveloperId)).Distinct(StringComparer.Ordinal);
            foreach (var developerId in developers)
            {
                var index = state.AvailableSources.FirstOrDefault(source => source.Source.DeveloperId == developerId);
                var removed = delisted.Where(metadata => metadata.External!.Manifest.DeveloperId == developerId).ToArray();
                var rows = new List<UIElement>();
                if (index is not null)
                    foreach (var entry in index.Plugins.Where(_library.Catalog.ChannelAllows))
                    {
                        var installed = installedPlugins.FirstOrDefault(metadata => metadata.External!.Manifest.Id == entry.Id);
                        if (installed is not null && CatalogVersion.Parse(entry.Version).CompareTo(CatalogVersion.Parse(installed.Version)) < 0) continue;
                        rows.Add(CreateEntry(entry, installed));
                    }
                foreach (var metadata in removed) rows.Add(CreateDelisted(metadata));
                var name = developerId == PluginCatalogClient.OfficialDeveloperId ? PluginCatalogClient.OfficialDeveloperId
                    : index?.Source.DeveloperName ?? removed[0].External!.Manifest.DeveloperName ?? developerId;
                if (rows.Count > 0)
                {
                    var section = new SettingsSection(name, false, rows.ToArray());
                    SettingsSection.UpdateSeparators(section);
                    _plugins.Children.Add(section);
                }
            }
        }
        foreach (var notice in _notices) notice.Refresh();
        _empty.Visibility = !loading && state.Failure is null && !state.Unavailable && _plugins.Children.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        _status.Visibility = _empty.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        foreach (var (action, row) in _actions)
        {
            UpdateAction(action, row);
        }
        _save.IsEnabled = _refresh.IsEnabled = _operation is null && !state.Busy;
        _proxy.IsEnabled = _operation is null && !state.Busy;
        if (_detailCancel is not null) _detailCancel.Visibility = _operation is null ? Visibility.Collapsed : Visibility.Visible;
        if (_detailNotice is not null && _noticeKey is { } key) _detailNotice.Message = Localization.Get(key);
    }

    private (IconActionButton Install, IconActionButton Repository) EntryActions(PluginCatalogEntry entry)
    {
        var install = new IconActionButton(Symbol.Download, "PluginInstall", "OfficialInstall." + entry.Id) { Tag = entry };
        install.Click += async (_, _) => await RunAsync(async token =>
        {
            var confirmed = await ConfirmPermissionsAsync(entry, install.XamlRoot);
            if (confirmed is not null)
            {
                await _library.InstallOfficialAsync(entry, confirmed, token);
                Notice("PluginInstalledSuccess", InfoBarSeverity.Success);
            }
        });
        var repository = new IconActionButton(Symbol.Link, "PluginRepository", "OfficialRepository." + entry.Id);
        repository.Click += async (_, _) =>
        {
            try { await Windows.System.Launcher.LaunchUriAsync(new Uri(entry.RepositoryUrl)); }
            catch (Exception error) { Diagnostics.Capture(DiagnosticArea.Application, error, "OpenPluginRepository"); }
        };
        return (install, repository);
    }

    private void UpdateAction(IconActionButton action, SettingsRow row)
    {
        if (action.Tag is not PluginCatalogEntry entry) return;
        var installed = _library.Manager.Snapshot().FirstOrDefault(s => s.Definition.Id == entry.Id)?.Definition.Metadata;
        var reason = PluginLibrary.InstallUnavailableReason(entry, installed, _library.Catalog.IncludePrereleases);
        action.IsEnabled = !_disposed && _operation is null && !_library.Catalog.State.Busy && reason is null;
        var author = _library.Catalog.State.AvailableSources.FirstOrDefault(source => source.Source.DeveloperId == entry.DeveloperId)?.Source.DeveloperName;
        var summary = string.Join(" · ", string.IsNullOrWhiteSpace(author) ? entry.DeveloperId : author, Localization.Get("PluginOfficial"));
        row.Detail = string.IsNullOrEmpty(reason) ? summary : summary + Environment.NewLine + Localization.Get(reason);
        action.SetAction(installed is null ? 0xE896 : 0xE777, installed is null ? "PluginInstall" : "PluginUpdate");
    }

    private UIElement CreateEntry(PluginCatalogEntry entry, PluginMetadata? installed)
    {
        var (install, repository) = EntryActions(entry);
        var row = new SettingsLinkRow(new SymbolIcon(Symbol.AllApps), entry.Name, "OfficialPlugin." + entry.Id,
            localizeTitle: false, actions: [install, repository]);
        _actions.Add((install, row.Header));
        row.Header.Status = entry.Version;
        row.OpenRequested += () => OpenEntry(entry);
        return WithNotice(row, installed?.External?.Manifest);
    }

    private void OpenEntry(PluginCatalogEntry entry)
    {
        if (_disposed) return;
        var (install, repository) = EntryActions(entry);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = SettingsTokens.ControlSpacing };
        toolbar.Children.Add(install);
        toolbar.Children.Add(repository);
        var cancel = new IconActionButton(Symbol.Cancel, "PluginCancel", "CancelPluginDetailInstallation") { Visibility = Visibility.Collapsed };
        cancel.Click += (_, _) => _operation?.Cancel();
        toolbar.Children.Add(cancel);
        _detailCancel = cancel;
        var row = new SettingsRow(Symbol.AllApps, entry.Name, toolbar, localizeTitle: false) { Status = entry.Version };
        var details = new SettingsContent(false);
        var notice = new InfoBar { IsOpen = true, IsClosable = false, Visibility = Visibility.Collapsed };
        _detailNotice = notice;
        details.Children.Add(notice);
        var basic = new SettingsContent(false,
            new SettingsField("PluginDescription", entry.Description, "OfficialDescription." + entry.Id),
            new PluginCopyField("PluginIdentifier", entry.Id, "OfficialIdentifier." + entry.Id, "PluginCopyIdentifier"),
            new SettingsField("PluginVersion", entry.Version, "OfficialVersion." + entry.Id),
            new SettingsField("PluginDeveloper", entry.DeveloperId, "OfficialDeveloper." + entry.Id),
            new SettingsField("PluginRepository", entry.RepositoryUrl, "OfficialRepositoryValue." + entry.Id));
        details.Children.Add(new SettingsSection("PluginBasicInformation", basic));
        var permissions = new SettingsField("PluginPermissionsRequested", string.Join(", ", entry.Permissions), "OfficialPermissions." + entry.Id, placeholder: false);
        if (entry.Permissions.Length == 0) LocalizedUI.Bind(permissions, () => permissions.Value.Text = Localization.Get("PluginPermissionsNone"));
        var permissionNotice = LocalizedUI.Text(entry.DeveloperId == PluginCatalogClient.OfficialDeveloperId ? "PluginOfficialAuthor" : "PluginThirdParty", SettingsTokens.DescriptionTextSize, secondary: true);
        permissionNotice.TextWrapping = TextWrapping.Wrap;
        details.Children.Add(new SettingsSection("PluginPermissionGroup", new SettingsContent(false, permissions, permissionNotice)));
        _open(new(entry.Name, false, SettingsSection.Page(row, details), () => UpdateAction(install, row),
            Close: () => { if (ReferenceEquals(_detailNotice, notice)) { _detailNotice = null; _detailCancel = null; } },
            IsCurrent: () => _library.Catalog.State.AvailableSources.Any(s => s.Plugins.Contains(entry))));
        UpdateAction(install, row);
    }

    private UIElement CreateDelisted(PluginMetadata metadata)
    {
        var manifest = metadata.External!.Manifest;
        var row = new SettingsLinkRow(new SymbolIcon(Symbol.AllApps), manifest.Name, "OfficialPlugin." + manifest.Id,
            localizeTitle: false);
        row.Header.Status = metadata.Version;
        row.OpenRequested += async () =>
        {
            if (_disposed) return;
            var details = new SettingsPluginDetails(new(manifest.Id, manifest.Name, "", 0, DiagnosticArea.Application,
                _ => false, (settings, _) => settings, _ => false, metadata), _library.Catalog);
            _open(new(manifest.Name, false, SettingsSection.Page(details), details.RefreshCatalog, details.Dispose));
            await details.ReadSizeAsync();
        };
        return WithNotice(row, manifest);
    }

    private UIElement WithNotice(SettingsLinkRow row, PluginManifest? manifest)
    {
        var notice = new PluginCatalogNotice(_library.Catalog, manifest, inset: true);
        if (manifest is not null) AutomationProperties.SetAutomationId(notice, "OfficialCatalogNotice." + manifest.Id);
        _notices.Add(notice);
        return new SettingsListItem(row, notice);
    }

    private async Task<string[]?> ConfirmPermissionsAsync(PluginCatalogEntry entry, XamlRoot xamlRoot)
    {
        _dialog = PluginConsentDialog.CreateInstall(entry);
        _dialog.XamlRoot = xamlRoot;
        if (_dialog.Content is ScrollViewer scroll) scroll.MaxHeight = Math.Max(80, Math.Min(320, xamlRoot.Size.Height - 200));
        try { return await _dialog.ShowAsync() == ContentDialogResult.Primary ? [] : null; }
        finally { _dialog = null; }
    }

    private async Task RunAsync(Func<CancellationToken, Task> run)
    {
        if (_operation is not null || _disposed) return;
        using var operation = _operation = new CancellationTokenSource();
        _cancel.Visibility = Visibility.Visible;
        _notice.Visibility = Visibility.Collapsed;
        if (_detailNotice is not null) _detailNotice.Visibility = Visibility.Collapsed;
        Refresh();
        try { await Task.Yield(); await run(operation.Token); }
        catch (OperationCanceledException) { Notice("PluginInstallCanceled", InfoBarSeverity.Informational); }
        catch (Exception error)
        {
            if (error is not PluginException { Constraint: "C35" })
                Diagnostics.Capture(DiagnosticArea.Application, error, "OfficialPluginOperation");
            Notice(PluginErrors.Key(error), InfoBarSeverity.Error);
        }
        finally
        {
            _operation = null;
            if (!_disposed) { _cancel.Visibility = Visibility.Collapsed; Refresh(); }
        }
    }

    private void Notice(string key, InfoBarSeverity severity)
    {
        if (_disposed) return;
        _notice.Message = Localization.Get(key);
        _noticeKey = key;
        _notice.Severity = severity;
        _notice.Visibility = Visibility.Visible;
        if (_detailNotice is not null)
        {
            _detailNotice.Message = Localization.Get(key);
            _detailNotice.Severity = severity;
            _detailNotice.Visibility = Visibility.Visible;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _dialog?.Hide();
        _operation?.Cancel();
        _library.Catalog.Changed -= OnChanged;
    }
}
