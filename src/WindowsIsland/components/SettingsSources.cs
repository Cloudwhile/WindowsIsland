using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class SettingsSources : StackPanel, IDisposable
{
    private sealed record Controls(SettingsLinkRow Row, NotificationPluginDefinition Definition, ToggleSwitch Toggle,
        IconActionButton Retry);
    private readonly SettingsStore _store;
    private readonly Action<Exception> _failed;
    private readonly Func<string, Task> _retry;
    private readonly Func<string, bool, Task> _setEnabled;
    private readonly PluginCatalogService? _catalog;
    private readonly Func<string, Task<PluginSettingsModel?>>? _settings;
    private readonly Action<SettingsDetail> _open;
    private readonly Dictionary<string, Controls> _rows = [];
    private readonly HashSet<string> _changing = [];
    private bool _syncing, _disposed;

    public SettingsSources(SettingsStore store, Action<Exception> failed, Func<string, Task> retry,
        Func<string, bool, Task>? setEnabled = null, Func<string, Task>? remove = null, PluginCatalogService? catalog = null,
        Func<string, Task<PluginSettingsModel?>>? settings = null, Action<SettingsDetail>? open = null)
    {
        _store = store;
        _failed = failed;
        _retry = retry;
        _setEnabled = setEnabled ?? ((_, _) => Task.CompletedTask);
        _catalog = catalog;
        _settings = settings;
        _open = open ?? (_ => { });
    }

    public void Refresh(IReadOnlyList<NotificationPluginStatus> plugins, bool busy)
    {
        if (_disposed) return;
        _syncing = true;
        try
        {
            var systemAvailable = plugins.Any(plugin => plugin.Definition.Id == BuiltInPluginIds.System && SourceStatus.IsRunning(plugin.StatusKey));
            var ids = plugins.Select(plugin => plugin.Definition.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _rows.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                var removed = _rows[id];
                Children.Remove(removed.Row);
                _rows.Remove(id);
            }
            foreach (var plugin in plugins)
            {
                var definition = plugin.Definition;
                if (_rows.TryGetValue(definition.Id, out var old) && !ReferenceEquals(old.Definition.Metadata, definition.Metadata))
                {
                    Children.Remove(old.Row);
                    _rows.Remove(definition.Id);
                }
                if (!_rows.TryGetValue(definition.Id, out var controls))
                {
                    var toggle = new ToggleSwitch { OnContent = null, OffContent = null, MinWidth = 0,
                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                    if (definition.Metadata?.External is null) LocalizedUI.Label(toggle, definition.TitleKey);
                    else AutomationProperties.SetName(toggle, definition.TitleKey);
                    AutomationProperties.SetAutomationId(toggle, definition.ToggleId);
                    toggle.Toggled += async (_, _) =>
                    {
                        if (_syncing || _disposed || !_changing.Add(definition.Id)) return;
                        toggle.IsEnabled = false;
                        try { await _setEnabled(definition.Id, toggle.IsOn); }
                        catch (Exception error) { _failed(error); }
                        finally
                        {
                            _changing.Remove(definition.Id);
                            if (!_disposed)
                            {
                                _syncing = true;
                                try { toggle.IsOn = definition.IsEnabled(_store.Current); }
                                finally { _syncing = false; }
                                toggle.IsEnabled = true;
                            }
                        }
                    };
                    var retry = new IconActionButton(Symbol.Refresh, definition.RetryLabelKey, "SourceRetry." + definition.Id)
                    { Visibility = Visibility.Collapsed };
                    retry.Click += async (_, _) =>
                    {
                        if (_disposed || !_changing.Add(definition.Id)) return;
                        retry.IsEnabled = false;
                        try { await _retry(definition.Id); }
                        catch (Exception error) { _failed(error); }
                        finally { _changing.Remove(definition.Id); if (!_disposed) retry.IsEnabled = true; }
                    };
                    var row = new SettingsLinkRow(new FontIcon { Glyph = char.ConvertFromUtf32(definition.Icon), FontSize = 20 },
                        definition.TitleKey, "Plugin." + definition.Id, "SourceStatus." + definition.Id, definition.Metadata?.External is null, retry, toggle);
                    row.OpenRequested += async () =>
                    {
                        if (_disposed) return;
                        var details = new SettingsPluginDetails(definition, _catalog, _settings);
                        _open(new(definition.TitleKey, definition.Metadata?.External is null, SettingsSection.Page(details),
                            details.RefreshCatalog, details.Dispose,
                            () => _rows.TryGetValue(definition.Id, out var current) && ReferenceEquals(current.Definition.Metadata, definition.Metadata)));
                        await details.ReadSizeAsync();
                    };
                    controls = new(row, definition, toggle, retry);
                    _rows.Add(definition.Id, controls);
                    Children.Add(controls.Row);
                }
                controls.Toggle.IsOn = definition.IsEnabled(_store.Current);
                controls.Toggle.IsEnabled = !busy && !_changing.Contains(definition.Id);
                var title = definition.Metadata?.External is null ? Localization.Get(definition.TitleKey) : definition.TitleKey;
                AutomationProperties.SetName(controls.Toggle, Localization.Format(controls.Toggle.IsOn ? "SourceEnabledLabel" : "SourceDisabledLabel", title));
                var key = controls.Toggle.IsOn ? plugin.StatusKey : "Off";
                controls.Row.Header.Status = Localization.Get(key);
                controls.Row.Header.Tone = SourceStatus.Tone(key);
                controls.Row.Header.Detail = SourceStatus.DetailKey(definition.Id, key, systemAvailable) is { } detail ? Localization.Get(detail) : "";
                controls.Retry.Visibility = controls.Toggle.IsOn && plugin.CanRetry ? Visibility.Visible : Visibility.Collapsed;
                controls.Retry.IsEnabled = !busy && !_changing.Contains(definition.Id);
            }
        }
        finally { _syncing = false; }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
