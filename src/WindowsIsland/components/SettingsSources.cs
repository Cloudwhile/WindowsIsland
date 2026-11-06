using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class SettingsSources : StackPanel, IDisposable
{
    private sealed record Controls(SettingsLinkRow Row, NotificationPluginDefinition Definition, ToggleSwitch Toggle,
        IconActionButton Retry, ProgressRing Progress);
    private readonly SettingsStore _store;
    private readonly Action<Exception> _failed;
    private readonly Func<string, Task> _retry;
    private readonly Func<string, bool, Task> _setEnabled;
    private readonly PluginCatalogService? _catalog;
    private readonly Func<string, Task<PluginSettingsModel?>>? _settings;
    private readonly Action<SettingsDetail> _open;
    private readonly Dictionary<string, Controls> _rows = [];
    private readonly HashSet<string> _changing = [];
    private NotificationPluginStatus[]? _shown;
    private AppSettings? _shownSettings;
    private string? _shownLanguage;
    private bool _shownBusy;
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
        var settings = _store.Current;
        if (_shown is { } shown && shown.Length == plugins.Count && ReferenceEquals(_shownSettings, settings)
            && _shownLanguage == Localization.Language && _shownBusy == busy && _changing.Count == 0
            && Enumerable.Range(0, plugins.Count).All(index => shown[index] == plugins[index])) return;
        _shown = plugins.ToArray();
        _shownSettings = settings;
        _shownLanguage = Localization.Language;
        _shownBusy = busy;
        _syncing = true;
        try
        {
            var structureChanged = false;
            var systemAvailable = plugins.Any(plugin => plugin.Definition.Id == BuiltInPluginIds.System && SourceStatus.IsRunning(plugin.StatusKey));
            var ids = plugins.Select(plugin => plugin.Definition.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _rows.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                var removed = _rows[id];
                Children.Remove(removed.Row);
                _rows.Remove(id);
                structureChanged = true;
            }
            foreach (var plugin in plugins)
            {
                var definition = plugin.Definition;
                if (_rows.TryGetValue(definition.Id, out var old) && !ReferenceEquals(old.Definition.Metadata, definition.Metadata))
                {
                    Children.Remove(old.Row);
                    _rows.Remove(definition.Id);
                    structureChanged = true;
                }
                if (!_rows.TryGetValue(definition.Id, out var controls))
                {
                    var progress = new ProgressRing { Width = 20, Height = 20, IsActive = false,
                        IsTabStop = false, Visibility = Visibility.Collapsed };
                    AutomationProperties.SetAutomationId(progress, "SourceProgress." + definition.Id);
                    LocalizedUI.SetAutomationName(progress, "Busy");
                    var toggle = new ToggleSwitch { OnContent = null, OffContent = null, MinWidth = 0,
                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                    if (definition.Metadata?.External is null) LocalizedUI.Label(toggle, definition.TitleKey);
                    else AutomationProperties.SetName(toggle, definition.TitleKey);
                    AutomationProperties.SetAutomationId(toggle, definition.ToggleId);
                    toggle.Toggled += async (_, _) =>
                    {
                        if (_syncing || !StartChange(definition.Id)) return;
                        try { await _setEnabled(definition.Id, toggle.IsOn); }
                        catch (Exception error) { _failed(error); }
                        finally
                        {
                            if (!_disposed)
                            {
                                _syncing = true;
                                try { toggle.IsOn = definition.IsEnabled(_store.Current); }
                                finally { _syncing = false; }
                            }
                            FinishChange(definition.Id);
                        }
                    };
                    var retry = new IconActionButton(Symbol.Refresh, definition.RetryLabelKey, "SourceRetry." + definition.Id)
                    { Visibility = Visibility.Collapsed };
                    retry.Click += async (_, _) =>
                    {
                        if (!StartChange(definition.Id)) return;
                        try { await _retry(definition.Id); }
                        catch (Exception error) { _failed(error); }
                        finally { FinishChange(definition.Id); }
                    };
                    var row = new SettingsLinkRow(new FontIcon { Glyph = char.ConvertFromUtf32(definition.Icon), FontSize = 20 },
                        definition.TitleKey, "Plugin." + definition.Id, "SourceStatus." + definition.Id, definition.Metadata?.External is null, progress, retry, toggle);
                    row.OpenRequested += async () =>
                    {
                        if (_disposed) return;
                        var details = new SettingsPluginDetails(definition, _catalog, _settings);
                        _open(new(definition.TitleKey, definition.Metadata?.External is null, SettingsSection.Page(details),
                            details.RefreshCatalog, details.Dispose,
                            () => _rows.TryGetValue(definition.Id, out var current) && ReferenceEquals(current.Definition.Metadata, definition.Metadata)));
                        await details.ReadSizeAsync();
                    };
                    controls = new(row, definition, toggle, retry, progress);
                    _rows.Add(definition.Id, controls);
                    Children.Add(controls.Row);
                    structureChanged = true;
                }
                var changing = _changing.Contains(definition.Id);
                controls.Toggle.IsOn = definition.IsEnabled(_store.Current);
                controls.Toggle.IsEnabled = !busy && !changing;
                ShowProgress(controls.Progress, changing);
                var title = definition.Metadata?.External is null ? Localization.Get(definition.TitleKey) : definition.TitleKey;
                AutomationProperties.SetName(controls.Toggle, Localization.Format(controls.Toggle.IsOn ? "SourceEnabledLabel" : "SourceDisabledLabel", title));
                var key = controls.Toggle.IsOn ? plugin.StatusKey : "Off";
                controls.Row.Header.Status = Localization.Get(key);
                controls.Row.Header.Tone = SourceStatus.Tone(key);
                controls.Row.Header.Detail = SourceStatus.DetailKey(definition.Id, key, systemAvailable) is { } detail ? Localization.Get(detail) : "";
                controls.Retry.Visibility = controls.Toggle.IsOn && plugin.CanRetry ? Visibility.Visible : Visibility.Collapsed;
                controls.Retry.IsEnabled = !busy && !changing;
            }
            if (structureChanged) SettingsSection.UpdateSeparators(this);
        }
        finally { _syncing = false; }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var controls in _rows.Values) ShowProgress(controls.Progress, false);
    }

    private bool StartChange(string id)
    {
        if (_disposed || !_changing.Add(id)) return false;
        if (_rows.TryGetValue(id, out var controls))
        {
            controls.Toggle.IsEnabled = controls.Retry.IsEnabled = false;
            ShowProgress(controls.Progress, true);
        }
        return true;
    }

    private void FinishChange(string id)
    {
        _changing.Remove(id);
        _shown = null;
        if (_rows.TryGetValue(id, out var controls))
        {
            ShowProgress(controls.Progress, false);
            if (!_disposed) controls.Toggle.IsEnabled = controls.Retry.IsEnabled = !_shownBusy;
        }
    }

    private static void ShowProgress(ProgressRing progress, bool active)
    {
        progress.IsActive = active;
        progress.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }
}
