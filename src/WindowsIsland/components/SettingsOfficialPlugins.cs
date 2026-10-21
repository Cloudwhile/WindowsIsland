using System.Diagnostics;
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
    private readonly List<IconActionButton> _actions = [];
    private PluginCatalogState? _rendered;
    private string _lastBase = "";
    private CancellationTokenSource? _operation;
    private ContentDialog? _dialog;
    private bool _disposed;
    private string? _noticeKey;

    public SettingsOfficialPlugins(PluginLibrary library)
    {
        _library = library;
        Spacing = 8;
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        toolbar.Children.Add(_save);
        toolbar.Children.Add(_refresh);
        toolbar.Children.Add(_cancel);
        Children.Add(new SettingsRow(Symbol.World, "PluginOfficial", toolbar));
        _address.Text = library.Catalog.State.BaseUrl;
        _address.Margin = new Thickness(12, 0, 12, 0);
        LocalizedUI.Label(_address, "PluginSourceAddress");
        AutomationProperties.SetAutomationId(_address, "PluginSourceAddress");
        Children.Add(_address);
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = _notice.Margin = new Thickness(12, 0, 12, 0);
        Children.Add(_status);
        Children.Add(_notice);
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
        if (_lastBase != state.BaseUrl && _operation is null) { _lastBase = state.BaseUrl; _address.Text = state.BaseUrl; }
        _status.Text = Localization.Get(state.Unavailable ? "PluginCatalogUnavailable" : state.Sources.Length == 0 ? "PluginCatalogEmpty" : "PluginCatalogReady");
        if (!ReferenceEquals(_rendered, state) && _operation is null)
        {
            _rendered = state;
            _plugins.Children.Clear();
            _actions.Clear();
            foreach (var index in state.Sources)
            {
                var developer = IslandTheme.Text(index.Source.DeveloperName, 16);
                developer.TextWrapping = TextWrapping.Wrap;
                developer.Margin = new Thickness(12, 12, 12, 4);
                _plugins.Children.Add(developer);
                foreach (var entry in index.Plugins) Add(entry);
            }
        }
        foreach (var action in _actions)
        {
            if (action.Tag is not PluginCatalogEntry entry) continue;
            var installed = _library.Manager.Snapshot().FirstOrDefault(s => s.Definition.Id == entry.Id)?.Definition.Metadata;
            var newer = installed is null || ReleaseVersion.FromInformationalVersion(entry.Version).CompareTo(ReleaseVersion.FromInformationalVersion(installed.Version)) > 0;
            var sameOwner = installed?.External is not { } external || external.Manifest.SourceKind == PluginSourceKind.Official
                && external.Manifest.DeveloperId == entry.DeveloperId;
            action.IsEnabled = _operation is null && newer && sameOwner && PluginLibrary.Compatible(entry);
            action.SetAction(installed is null ? 0xE896 : 0xE777, installed is null ? "PluginInstall" : "PluginUpdate");
        }
        _save.IsEnabled = _refresh.IsEnabled = _operation is null && !state.Busy;
        _address.IsEnabled = _operation is null && !state.Busy;
    }

    private void Add(PluginCatalogEntry entry)
    {
        var install = new IconActionButton(Symbol.Download, "PluginInstall", "OfficialInstall." + entry.Id) { Tag = entry };
        install.Click += async (_, _) => await RunAsync(async token =>
        {
            var confirmed = await ConfirmPermissionsAsync(entry);
            if (confirmed is null) return;
            await _library.InstallOfficialAsync(entry, confirmed, token);
        });
        _actions.Add(install);
        var repository = new IconActionButton(Symbol.Link, "PluginRepository", "OfficialRepository." + entry.Id);
        repository.Click += async (_, _) =>
        {
            try { await Windows.System.Launcher.LaunchUriAsync(new Uri(entry.RepositoryUrl)); }
            catch (Exception error) { Diagnostics.Capture(DiagnosticArea.Application, error, "OpenPluginRepository"); }
        };
        var row = new SettingsExpander(new SymbolIcon(Symbol.AllApps), entry.Name, "OfficialPlugin." + entry.Id,
            localizeTitle: false, actions: [install, repository]);
        row.Header.Status = entry.Version;
        row.ExpandOnHeaderTap = true;
        var description = IslandTheme.Text(entry.Description, 14);
        description.TextWrapping = TextWrapping.Wrap;
        description.Margin = new Thickness(0, 8, 12, 8);
        row.Rows.Add(description);
        var permissions = IslandTheme.Text(string.Join(", ", entry.Permissions), 12);
        permissions.TextWrapping = TextWrapping.Wrap;
        row.Rows.Add(permissions);
        var trust = LocalizedUI.Text(entry.DeveloperId == PluginCatalogClient.OfficialDeveloperId ? "PluginOfficialAuthor" : "PluginThirdParty", 12, secondary: true);
        trust.Margin = new Thickness(0, 4, 0, 12);
        row.Rows.Add(trust);
        _plugins.Children.Add(row);
    }

    private async Task<string[]?> ConfirmPermissionsAsync(PluginCatalogEntry entry)
    {
        if (entry.DeveloperId == PluginCatalogClient.OfficialDeveloperId) return entry.Permissions.Where(PluginPermissions.IsHighRisk).ToArray();
        var content = new StackPanel { Spacing = 8 };
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
        _library.Catalog.Changed -= OnChanged;
    }
}
