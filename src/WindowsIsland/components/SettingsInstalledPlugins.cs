using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

/// <summary>复用宿主管理器的已安装插件列表，不保存第二份启用状态。</summary>
internal sealed class SettingsInstalledPlugins : StackPanel, IDisposable
{
    private sealed record Entry(NotificationPluginDefinition Definition, SettingsLinkRow Row, PluginCatalogNotice Notice);
    private readonly PluginLibrary _library;
    private readonly Action<SettingsDetail> _open;
    private readonly Action? _openDiagnostics;
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly StackPanel _list = new();
    private readonly SettingsContent _empty = new(false);
    private readonly InfoBar _notice = new() { IsOpen = true, IsClosable = false, Visibility = Visibility.Collapsed };
    private ContentDialog? _dialog;
    private string? _noticeKey;
    private bool _busy, _syncing, _disposed;

    public SettingsInstalledPlugins(PluginLibrary library, Action<SettingsDetail> open, Action? openDiagnostics = null)
    {
        _library = library;
        _open = open;
        _openDiagnostics = openDiagnostics;
        var icon = new SymbolIcon(Symbol.AllApps) { HorizontalAlignment = HorizontalAlignment.Left };
        _empty.Children.Add(icon);
        _empty.Children.Add(SettingsSection.Heading("PluginInstalledEmpty"));
        var hint = LocalizedUI.Text("PluginInstalledEmptyHint", SettingsTokens.DescriptionTextSize, secondary: true);
        hint.TextWrapping = TextWrapping.Wrap;
        _empty.Children.Add(hint);
        Children.Add(SettingsContent.Wrap(_notice));
        Children.Add(_empty);
        Children.Add(_list);
        LocalizedUI.Bind(this, () =>
        {
            if (_noticeKey is { } key) _notice.Message = Localization.Get(key);
            Refresh();
        });
        Refresh();
    }

    public void Refresh()
    {
        if (_disposed) return;
        var plugins = _library.Manager.Snapshot();
        var ids = plugins.Select(item => item.Definition.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _entries.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            _list.Children.Remove(_entries[id].Row);
            _entries.Remove(id);
        }
        foreach (var plugin in plugins)
        {
            var definition = plugin.Definition;
            if (_entries.TryGetValue(definition.Id, out var old) && !ReferenceEquals(old.Definition.Metadata, definition.Metadata))
            {
                _list.Children.Remove(old.Row);
                _entries.Remove(definition.Id);
            }
            if (!_entries.TryGetValue(definition.Id, out var entry))
            {
                var row = new SettingsLinkRow(new FontIcon { Glyph = char.ConvertFromUtf32(definition.Icon > 0 ? definition.Icon : (int)Symbol.AllApps), FontSize = SettingsTokens.IconSize },
                    definition.TitleKey, "InstalledPlugin." + definition.Id, "InstalledPluginStatus." + definition.Id,
                    definition.Metadata?.External is null);
                row.OpenRequested += () => Open(definition);
                var catalogNotice = new PluginCatalogNotice(_library.Catalog, definition.Metadata?.External?.Manifest, inset: true);
                AutomationProperties.SetAutomationId(catalogNotice, "InstalledPluginCatalogNotice." + definition.Id);
                row.Children.Add(catalogNotice);
                entry = new(definition, row, catalogNotice);
                _entries.Add(definition.Id, entry);
                _list.Children.Add(row);
            }
            entry.Row.Header.Status = Localization.Get(plugin.StatusKey);
            entry.Row.Header.Tone = SourceStatus.Tone(plugin.StatusKey);
            entry.Row.Header.Detail = Summary(definition.Metadata);
            entry.Notice.Refresh();
        }
        _empty.Visibility = plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Summary(PluginMetadata? metadata)
    {
        var source = metadata?.External is { } external
            ? external.Manifest.SourceKind == PluginSourceKind.Manual ? "PluginManualRestricted" : "PluginOfficial"
            : metadata?.IsBuiltIn != false ? "PluginBuiltIn" : "PluginInstalled";
        return string.Join(" · ", string.IsNullOrWhiteSpace(metadata?.Author) ? Localization.Get("PluginUnknown") : metadata.Author,
            string.IsNullOrWhiteSpace(metadata?.Version) ? Localization.Get("PluginUnknown") : metadata.Version, Localization.Get(source));
    }

    private async void Open(NotificationPluginDefinition definition)
    {
        if (_disposed) return;
        var details = new SettingsPluginDetails(definition, _library.Catalog, _library.Manager.GetSettingsAsync);
        var enabled = new ToggleSwitch { OnContent = null, OffContent = null, MinWidth = 0 };
        AutomationProperties.SetAutomationId(enabled, "InstalledPluginEnabled." + definition.Id);
        if (definition.Metadata?.External is null) LocalizedUI.Label(enabled, definition.TitleKey);
        else AutomationProperties.SetName(enabled, definition.TitleKey);
        var remove = new IconActionButton(Symbol.Delete, "ActionRemovePlugin", "InstalledPluginRemove." + definition.Id)
        { Visibility = definition.CanRemove ? Visibility.Visible : Visibility.Collapsed };
        var retry = new IconActionButton(Symbol.Refresh, definition.RetryLabelKey, "InstalledPluginRetry." + definition.Id);
        var diagnostics = new IconActionButton(Symbol.Help, "DiagnosticsTitle", "InstalledPluginDiagnostics." + definition.Id);
        diagnostics.Click += (_, _) => _openDiagnostics?.Invoke();
        var progress = new ProgressRing { Width = 20, Height = 20, IsActive = false, Visibility = Visibility.Collapsed };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = SettingsTokens.ControlSpacing };
        toolbar.Children.Add(progress);
        toolbar.Children.Add(diagnostics);
        toolbar.Children.Add(retry);
        toolbar.Children.Add(enabled);
        toolbar.Children.Add(remove);
        var header = new SettingsRow(new FontIcon { Glyph = char.ConvertFromUtf32(definition.Icon > 0 ? definition.Icon : (int)Symbol.AllApps), FontSize = SettingsTokens.IconSize },
            definition.TitleKey, toolbar, localizeTitle: definition.Metadata?.External is null);
        var notice = new InfoBar { IsOpen = true, IsClosable = false, Visibility = Visibility.Collapsed };
        var closed = false;
        string? detailNoticeKey = null;
        void RefreshDetail()
        {
            if (_disposed || closed) return;
            var current = _library.Manager.Snapshot().FirstOrDefault(item => item.Definition.Id == definition.Id);
            _syncing = true;
            try { enabled.IsOn = current?.StatusKey is not (null or "Off"); }
            finally { _syncing = false; }
            enabled.IsEnabled = remove.IsEnabled = retry.IsEnabled = !_busy;
            progress.IsActive = _busy;
            progress.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
            retry.Visibility = current?.CanRetry == true ? Visibility.Visible : Visibility.Collapsed;
            diagnostics.Visibility = _openDiagnostics is not null && current is not null
                && SourceStatus.Tone(current.StatusKey) == StatusTone.Critical ? Visibility.Visible : Visibility.Collapsed;
            header.Status = Localization.Get(current?.StatusKey ?? "Off");
            header.Tone = SourceStatus.Tone(current?.StatusKey ?? "Off");
            header.Detail = Summary(definition.Metadata);
            if (detailNoticeKey is { } key) notice.Message = Localization.Get(key);
            details.RefreshCatalog();
        }
        async Task RunAsync(Func<Task> action, string operation)
        {
            if (_busy || _disposed || closed) return;
            _busy = true;
            notice.Visibility = Visibility.Collapsed;
            RefreshDetail();
            try { await action(); }
            catch (Exception error)
            {
                new PluginDiagnostics(definition.Id, DiagnosticArea.Application).Capture(error, operation);
                if (!closed && !_disposed)
                {
                    detailNoticeKey = error is PluginException ? PluginErrors.Key(error) : "PluginOperationFailed";
                    notice.Message = Localization.Get(detailNoticeKey);
                    notice.Severity = InfoBarSeverity.Error;
                    notice.Visibility = Visibility.Visible;
                }
            }
            finally { _busy = false; Refresh(); RefreshDetail(); }
        }
        enabled.Toggled += async (_, _) =>
        {
            if (_syncing) return;
            var value = enabled.IsOn;
            await RunAsync(() => _library.Manager.SetEnabledAsync(definition.Id, value), "SettingsSave");
        };
        retry.Click += async (_, _) => await RunAsync(() => _library.Manager.RetryAsync(definition.Id), "Retry");
        remove.Click += async (_, _) => await RunAsync(async () =>
        {
            if (!definition.CanRemove || !await ConfirmRemoveAsync(definition, remove.XamlRoot)) return;
            await _library.Manager.RemoveAsync(definition.Id);
            if (!_disposed)
            {
                _noticeKey = "PluginRemovedSuccess";
                _notice.Message = Localization.Get(_noticeKey);
                _notice.Severity = InfoBarSeverity.Success;
                _notice.Visibility = Visibility.Visible;
            }
        }, "Uninstall");
        _open(new(definition.TitleKey, definition.Metadata?.External is null,
            SettingsSection.Page(header, SettingsContent.Wrap(notice), details), RefreshDetail,
            () => { closed = true; details.Dispose(); },
            () => _library.Manager.Snapshot().Any(item => item.Definition.Id == definition.Id && ReferenceEquals(item.Definition.Metadata, definition.Metadata))));
        RefreshDetail();
        await details.ReadSizeAsync();
    }

    private async Task<bool> ConfirmRemoveAsync(NotificationPluginDefinition definition, XamlRoot xamlRoot)
    {
        if (_disposed) return false;
        _dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = definition.Metadata?.External is null ? Localization.Get(definition.TitleKey) : definition.TitleKey,
            Content = Localization.Get("PluginRemoveQuestion"),
            PrimaryButtonText = Localization.Get("ActionRemovePlugin"),
            CloseButtonText = Localization.Get("PluginCancel"),
            DefaultButton = ContentDialogButton.Close
        };
        try { return await _dialog.ShowAsync() == ContentDialogResult.Primary && !_disposed; }
        finally { _dialog = null; }
    }

    public void Dispose()
    {
        _disposed = true;
        _dialog?.Hide();
    }
}
