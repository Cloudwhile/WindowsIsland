using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class SettingsSources : StackPanel, IDisposable
{
    private sealed record Controls(SettingsRow Row, ToggleSwitch Toggle, IconActionButton Retry);
    private readonly SettingsStore _store;
    private readonly Action<Exception> _failed;
    private readonly Func<string, Task> _retry;
    private readonly Dictionary<string, Controls> _rows = [];
    private bool _syncing, _disposed;

    public SettingsSources(SettingsStore store, Action<Exception> failed, Func<string, Task> retry)
    {
        _store = store;
        _failed = failed;
        _retry = retry;
    }

    public void Refresh(IReadOnlyList<NotificationPluginStatus> plugins, bool busy)
    {
        if (_disposed) return;
        _syncing = true;
        try
        {
            foreach (var plugin in plugins)
            {
                var definition = plugin.Definition;
                if (!_rows.TryGetValue(definition.Id, out var controls))
                {
                    var toggle = new ToggleSwitch { OnContent = null, OffContent = null, MinWidth = 0,
                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                    LocalizedUI.Label(toggle, definition.TitleKey);
                    AutomationProperties.SetAutomationId(toggle, definition.ToggleId);
                    toggle.Toggled += (_, _) =>
                    {
                        if (_syncing || _disposed) return;
                        try { _store.Save(definition.SetEnabled(_store.Current, toggle.IsOn)); }
                        catch (Exception error) { _failed(error); }
                        finally
                        {
                            _syncing = true;
                            try { toggle.IsOn = definition.IsEnabled(_store.Current); }
                            finally { _syncing = false; }
                        }
                    };
                    var retry = new IconActionButton(Symbol.Refresh, "ActionRetrySource", "SourceRetry." + definition.Id)
                    { Visibility = Visibility.Collapsed };
                    retry.Click += async (_, _) =>
                    {
                        if (_disposed) return;
                        retry.IsEnabled = false;
                        try { await _retry(definition.Id); }
                        catch (Exception error) { _failed(error); }
                        finally { retry.IsEnabled = true; }
                    };
                    var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                    actions.Children.Add(retry);
                    actions.Children.Add(toggle);
                    controls = new(new SettingsRow(new FontIcon { Glyph = char.ConvertFromUtf32(definition.Icon), FontSize = 20 },
                        definition.TitleKey, actions, "SourceStatus." + definition.Id), toggle, retry);
                    _rows.Add(definition.Id, controls);
                    Children.Add(controls.Row);
                }
                controls.Toggle.IsOn = definition.IsEnabled(_store.Current);
                controls.Toggle.IsEnabled = !busy;
                var key = controls.Toggle.IsOn ? plugin.StatusKey : "Off";
                controls.Row.Status = Localization.Get(key);
                controls.Row.Tone = SourceStatus.Tone(key);
                controls.Row.Detail = SourceStatus.DetailKey(definition.Id, key) is { } detail ? Localization.Get(detail) : "";
                controls.Retry.Visibility = controls.Toggle.IsOn && plugin.CanRetry ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        finally { _syncing = false; }
    }

    public void Dispose() => _disposed = true;
}
