using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class SettingsSources : StackPanel, IDisposable
{
    private sealed record Controls(SettingsExpander Expander, SettingsPluginDetails Details, ToggleSwitch Toggle,
        IconActionButton Retry, IconActionButton Delete);
    private readonly SettingsStore _store;
    private readonly Action<Exception> _failed;
    private readonly Func<string, Task> _retry;
    private readonly Func<string, bool, Task> _setEnabled;
    private readonly Func<string, Task> _remove;
    private readonly PluginCatalogService? _catalog;
    private readonly Func<string, Task<PluginSettingsModel?>>? _settings;
    private readonly Dictionary<string, Controls> _rows = [];
    private readonly TextBlock _retained = LocalizedUI.Text("PluginDataPreserved", 12, secondary: true);
    private bool _syncing, _disposed;
    private int _visit;

    public SettingsSources(SettingsStore store, Action<Exception> failed, Func<string, Task> retry,
        Func<string, bool, Task>? setEnabled = null, Func<string, Task>? remove = null, PluginCatalogService? catalog = null,
        Func<string, Task<PluginSettingsModel?>>? settings = null)
    {
        _store = store;
        _failed = failed;
        _retry = retry;
        _setEnabled = setEnabled ?? ((_, _) => Task.CompletedTask);
        _remove = remove ?? (_ => Task.CompletedTask);
        _catalog = catalog;
        _settings = settings;
        _retained.TextWrapping = TextWrapping.Wrap;
        _retained.Visibility = Visibility.Collapsed;
        Children.Add(SettingsContent.Wrap(_retained));
        Unloaded += (_, _) => { _visit++; _retained.Visibility = Visibility.Collapsed; };
    }

    public void Refresh(IReadOnlyList<NotificationPluginStatus> plugins, bool busy)
    {
        if (_disposed) return;
        _retained.Visibility = Visibility.Collapsed;
        _syncing = true;
        try
        {
            var systemAvailable = plugins.Any(plugin => plugin.Definition.Id == BuiltInPluginIds.System && SourceStatus.IsRunning(plugin.StatusKey));
            var ids = plugins.Select(plugin => plugin.Definition.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _rows.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                var removed = _rows[id];
                removed.Details.Dispose();
                Children.Remove(removed.Expander);
                _rows.Remove(id);
            }
            foreach (var plugin in plugins)
            {
                var definition = plugin.Definition;
                if (_rows.TryGetValue(definition.Id, out var old) && !old.Details.Matches(definition))
                {
                    old.Details.Dispose();
                    Children.Remove(old.Expander);
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
                        if (_syncing || _disposed) return;
                        try { await _setEnabled(definition.Id, toggle.IsOn); }
                        catch (Exception error) { _failed(error); }
                        finally
                        {
                            _syncing = true;
                            try { toggle.IsOn = definition.IsEnabled(_store.Current); }
                            finally { _syncing = false; }
                        }
                    };
                    var retry = new IconActionButton(Symbol.Refresh, definition.RetryLabelKey, "SourceRetry." + definition.Id)
                    { Visibility = Visibility.Collapsed };
                    retry.Click += async (_, _) =>
                    {
                        if (_disposed) return;
                        retry.IsEnabled = false;
                        try { await _retry(definition.Id); }
                        catch (Exception error) { _failed(error); }
                        finally { retry.IsEnabled = true; }
                    };
                    var delete = new IconActionButton(Symbol.Delete, "ActionRemovePlugin", "RemovePlugin." + definition.Id)
                    { Visibility = definition.CanRemove ? Visibility.Visible : Visibility.Collapsed };
                    delete.Click += async (_, _) =>
                    {
                        if (_disposed || !definition.CanRemove) return;
                        var visit = _visit;
                        delete.IsEnabled = false;
                        try
                        {
                            await _remove(definition.Id);
                            if (!_disposed && IsLoaded && visit == _visit && definition.Metadata?.External is not null)
                                _retained.Visibility = Visibility.Visible;
                        }
                        catch (Exception error) { _failed(error); }
                        finally { if (!_disposed) delete.IsEnabled = true; }
                    };
                    var expander = new SettingsExpander(new FontIcon { Glyph = char.ConvertFromUtf32(definition.Icon), FontSize = 20 },
                        definition.TitleKey, "Plugin." + definition.Id, "SourceStatus." + definition.Id, definition.Metadata?.External is null, retry, toggle, delete)
                    { ExpandOnHeaderTap = true };
                    var details = new SettingsPluginDetails(definition, _catalog, _settings);
                    expander.Rows.Add(details);
                    expander.ExpandedChanged += async expanded => { if (expanded) await details.ReadSizeAsync(); };
                    controls = new(expander, details, toggle, retry, delete);
                    _rows.Add(definition.Id, controls);
                    Children.Add(controls.Expander);
                }
                controls.Toggle.IsOn = definition.IsEnabled(_store.Current);
                controls.Details.RefreshCatalog();
                controls.Toggle.IsEnabled = !busy;
                var key = controls.Toggle.IsOn ? plugin.StatusKey : "Off";
                controls.Expander.Header.Status = Localization.Get(key);
                controls.Expander.Header.Tone = SourceStatus.Tone(key);
                controls.Expander.Header.Detail = SourceStatus.DetailKey(definition.Id, key, systemAvailable) is { } detail ? Localization.Get(detail) : "";
                controls.Retry.Visibility = controls.Toggle.IsOn && plugin.CanRetry ? Visibility.Visible : Visibility.Collapsed;
                controls.Delete.IsEnabled = !busy;
            }
        }
        finally { _syncing = false; }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var controls in _rows.Values) controls.Details.Dispose();
    }
}
