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
    private readonly TextBox _address = new() { MinWidth = 0 };
    private readonly IconActionButton _save = new(Symbol.Save, "PluginSaveSource", "SavePluginSource");
    private readonly IconActionButton _refresh = new(Symbol.Refresh, "PluginRefreshCatalog", "RefreshPluginCatalog");
    private readonly IconActionButton _cancel = new(Symbol.Cancel, "PluginCancel", "CancelOfficialPlugin") { Visibility = Visibility.Collapsed };
    private readonly StackPanel _plugins = new();
    private readonly TextBlock _status = LocalizedUI.Text("PluginCatalogEmpty", 12, secondary: true);
    private readonly InfoBar _notice = new() { IsOpen = true, IsClosable = false, Visibility = Visibility.Collapsed };
    private readonly List<(IconActionButton Button, SettingsRow Row)> _actions = [];
    private readonly List<PluginCatalogNotice> _notices = [];
    private readonly List<SettingsPluginDetails> _details = [];
    private PluginMetadata[] _installed = [];
    private PluginCatalogState? _rendered;
    private string _lastBase = "";
    private CancellationTokenSource? _operation;
    private ContentDialog? _dialog;
    private bool _disposed;
    private string? _noticeKey;

    public SettingsOfficialPlugins(PluginLibrary library)
    {
        _library = library;
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        toolbar.Children.Add(_save);
        toolbar.Children.Add(_refresh);
        toolbar.Children.Add(_cancel);
        _address.Text = library.Catalog.State.BaseUrl;
        LocalizedUI.Label(_address, "PluginSourceAddress");
        AutomationProperties.SetAutomationId(_address, "PluginSourceAddress");
        _status.TextWrapping = TextWrapping.Wrap;
        var source = new SettingsContent();
        source.Children.Add(_address);
        source.Children.Add(_status);
        source.Children.Add(_notice);
        Children.Add(new SettingsSection("PluginOfficial", new SettingsRow(Symbol.World, "PluginSourceAddress", toolbar), source));
        Children.Add(_plugins);
        _save.Click += async (_, _) => await RunAsync(async token => { await library.Catalog.ConfigureAsync(_address.Text); await library.Catalog.RefreshAsync(token); });
        _refresh.Click += async (_, _) => await RunAsync(token => library.Catalog.RefreshAsync(token));
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
        if (_lastBase != state.BaseUrl && _operation is null) { _lastBase = state.BaseUrl; _address.Text = state.BaseUrl; }
        _status.Text = Localization.Get(state.Unavailable ? "PluginCatalogUnavailable" : state.Sources.Length == 0 ? "PluginCatalogEmpty" : "PluginCatalogReady");
        if ((!ReferenceEquals(_rendered, state) || !_installed.SequenceEqual(installedPlugins)) && _operation is null)
        {
            _rendered = state;
            _installed = installedPlugins;
            foreach (var details in _details) details.Dispose();
            _details.Clear();
            _plugins.Children.Clear();
            _actions.Clear();
            _notices.Clear();
            var delisted = installedPlugins.Where(metadata => _library.Catalog.IsDelisted(metadata.External!.Manifest)).ToArray();
            var developers = state.Sources.Select(index => index.Source.DeveloperId)
                .Concat(delisted.Select(metadata => metadata.External!.Manifest.DeveloperId)).Distinct(StringComparer.Ordinal);
            foreach (var developerId in developers)
            {
                var index = state.Sources.FirstOrDefault(source => source.Source.DeveloperId == developerId);
                var removed = delisted.Where(metadata => metadata.External!.Manifest.DeveloperId == developerId).ToArray();
                var rows = new List<UIElement>();
                if (index is not null)
                    foreach (var entry in index.Plugins) rows.Add(CreateEntry(entry, installedPlugins.FirstOrDefault(metadata => metadata.External!.Manifest.Id == entry.Id)));
                foreach (var metadata in removed) rows.Add(CreateDelisted(metadata));
                var name = index?.Source.DeveloperName ?? removed[0].External!.Manifest.DeveloperName ?? developerId;
                _plugins.Children.Add(new SettingsSection(name, false, rows.ToArray()));
            }
        }
        foreach (var notice in _notices) notice.Refresh();
        foreach (var (action, row) in _actions)
        {
            if (action.Tag is not PluginCatalogEntry entry) continue;
            var installed = _library.Manager.Snapshot().FirstOrDefault(s => s.Definition.Id == entry.Id)?.Definition.Metadata;
            var reason = PluginLibrary.InstallUnavailableReason(entry, installed);
            action.IsEnabled = _operation is null && reason is null;
            row.Detail = reason is null ? "" : Localization.Get(reason);
            action.SetAction(installed is null ? 0xE896 : 0xE777, installed is null ? "PluginInstall" : "PluginUpdate");
        }
        _save.IsEnabled = _refresh.IsEnabled = _operation is null && !state.Busy;
        _address.IsEnabled = _operation is null && !state.Busy;
    }

    private UIElement CreateEntry(PluginCatalogEntry entry, PluginMetadata? installed)
    {
        var install = new IconActionButton(Symbol.Download, "PluginInstall", "OfficialInstall." + entry.Id) { Tag = entry };
        install.Click += async (_, _) => await RunAsync(async token =>
        {
            var confirmed = await ConfirmPermissionsAsync(entry);
            if (confirmed is null) return;
            await _library.InstallOfficialAsync(entry, confirmed, token);
        });
        var repository = new IconActionButton(Symbol.Link, "PluginRepository", "OfficialRepository." + entry.Id);
        repository.Click += async (_, _) =>
        {
            try { await Windows.System.Launcher.LaunchUriAsync(new Uri(entry.RepositoryUrl)); }
            catch (Exception error) { Diagnostics.Capture(DiagnosticArea.Application, error, "OpenPluginRepository"); }
        };
        var row = new SettingsExpander(new SymbolIcon(Symbol.AllApps), entry.Name, "OfficialPlugin." + entry.Id,
            localizeTitle: false, actions: [install, repository]);
        _actions.Add((install, row.Header));
        row.Header.Status = entry.Version;
        row.ExpandOnHeaderTap = true;
        var details = new SettingsContent(false);
        details.Children.Add(new SettingsField("PluginDescription", entry.Description, "OfficialDescription." + entry.Id));
        var permissions = new SettingsField("PluginPermissionsRequested", string.Join(", ", entry.Permissions), "OfficialPermissions." + entry.Id, placeholder: false);
        if (entry.Permissions.Length == 0) LocalizedUI.Bind(permissions, () => permissions.Value.Text = Localization.Get("PluginPermissionsNone"));
        details.Children.Add(permissions);
        var trust = LocalizedUI.Text(entry.DeveloperId == PluginCatalogClient.OfficialDeveloperId ? "PluginOfficialAuthor" : "PluginThirdParty", 12, secondary: true);
        details.Children.Add(trust);
        row.Rows.Add(details);
        return WithNotice(row, installed?.External?.Manifest);
    }

    private UIElement CreateDelisted(PluginMetadata metadata)
    {
        var manifest = metadata.External!.Manifest;
        var row = new SettingsExpander(new SymbolIcon(Symbol.AllApps), manifest.Name, "OfficialPlugin." + manifest.Id,
            localizeTitle: false) { ExpandOnHeaderTap = true };
        row.Header.Status = metadata.Version;
        var details = new SettingsPluginDetails(new(manifest.Id, manifest.Name, "", 0, DiagnosticArea.Application,
            _ => false, (settings, _) => settings, _ => false, metadata));
        _details.Add(details);
        row.Rows.Add(details);
        row.ExpandedChanged += async expanded => { if (expanded) await details.ReadSizeAsync(); };
        return WithNotice(row, manifest);
    }

    private UIElement WithNotice(SettingsExpander row, PluginManifest? manifest)
    {
        var container = new StackPanel();
        container.Children.Add(row);
        var notice = new PluginCatalogNotice(_library.Catalog, manifest, inset: true);
        if (manifest is not null) AutomationProperties.SetAutomationId(notice, "OfficialCatalogNotice." + manifest.Id);
        _notices.Add(notice);
        container.Children.Add(notice);
        return container;
    }

    private async Task<string[]?> ConfirmPermissionsAsync(PluginCatalogEntry entry)
    {
        if (entry.DeveloperId == PluginCatalogClient.OfficialDeveloperId) return entry.Permissions.Where(PluginPermissions.IsHighRisk).ToArray();
        var content = new SettingsContent(false);
        var text = LocalizedUI.Text("PluginThirdParty", 14);
        text.TextWrapping = TextWrapping.Wrap;
        content.Children.Add(text);
        var checks = entry.Permissions.Where(PluginPermissions.IsHighRisk).Select(permission => new CheckBox { Content = permission, Tag = permission }).ToArray();
        foreach (var check in checks)
        {
            AutomationProperties.SetAutomationId(check, "PluginPermission." + (string)check.Tag);
            content.Children.Add(check);
        }
        var unknown = entry.Permissions.Where(p => !PluginPermissions.IsKnown(p)).ToArray();
        if (unknown.Length > 0)
        {
            content.Children.Add(LocalizedUI.Text("PluginPermissionsDenied", 12, secondary: true));
            var names = IslandTheme.Text(string.Join(", ", unknown), 12);
            names.TextWrapping = TextWrapping.Wrap;
            content.Children.Add(names);
        }
        _dialog = new ContentDialog { XamlRoot = XamlRoot, Title = entry.Name,
            Content = new ScrollViewer { Content = content, MaxHeight = 320 },
            PrimaryButtonText = Localization.Get("PluginInstall"), CloseButtonText = Localization.Get("PluginCancel"),
            DefaultButton = ContentDialogButton.Close };
        try { return await _dialog.ShowAsync() == ContentDialogResult.Primary ? checks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToArray() : null; }
        finally { _dialog = null; }
    }

    private async Task RunAsync(Func<CancellationToken, Task> run)
    {
        if (_operation is not null || _disposed) return;
        using var operation = _operation = new CancellationTokenSource();
        _cancel.Visibility = Visibility.Visible;
        _notice.Visibility = Visibility.Collapsed;
        Refresh();
        try { await Task.Yield(); await run(operation.Token); }
        catch (OperationCanceledException) { Notice("PluginInstallCanceled", InfoBarSeverity.Informational); }
        catch (Exception error)
        {
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
    }

    public void Dispose()
    {
        _disposed = true;
        _dialog?.Hide();
        _operation?.Cancel();
        foreach (var details in _details) details.Dispose();
        _library.Catalog.Changed -= OnChanged;
    }
}
