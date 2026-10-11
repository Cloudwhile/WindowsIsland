using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class DoNotDisturbSettings : Grid, IDisposable
{
    private readonly DoNotDisturbPreferences _preferences;
    private readonly Func<Func<Task>, string, Task> _run;
    private readonly SettingsRow _row;
    private readonly ToggleSwitch _toggle = new()
    {
        OnContent = null, OffContent = null, MinWidth = 0,
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
    };
    private DoNotDisturbState _state;
    private bool _syncing, _enabled = true, _reading, _writing, _disposed;
    private int _revision;
    public IconActionButton OpenSettings { get; } = new(Symbol.Setting, "ActionOpenWindowsNotificationSettings", "OpenSystemNotificationSettings");

    public DoNotDisturbSettings(DoNotDisturbPreferences preferences, Func<Func<Task>, string, Task> run)
    {
        _preferences = preferences;
        _run = run;
        AutomationProperties.SetAutomationId(_toggle, "SystemDoNotDisturbToggle");
        LocalizedUI.Label(_toggle, "SystemDoNotDisturb");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(_toggle);
        actions.Children.Add(OpenSettings);
        _row = new SettingsRow(Symbol.Mute, "SystemDoNotDisturb", actions, "SystemDoNotDisturbStatus");
        Children.Add(_row);
        _toggle.Toggled += OnToggled;
        LocalizedUI.Bind(this, RefreshLabels);
    }

    public async Task RefreshAsync()
    {
        if (_disposed || _reading || _writing || !_enabled) return;
        _reading = true;
        var revision = _revision;
        try
        {
            var state = await Task.Run(_preferences.Read);
            if (!_disposed && revision == _revision) _state = state;
        }
        catch (Exception error)
        {
            Diagnostics.Capture(DiagnosticArea.WindowsSettings, error, "ReadDoNotDisturb");
            if (!_disposed && revision == _revision) _state = DoNotDisturbState.Unavailable;
        }
        finally
        {
            _reading = false;
            if (!_disposed && revision == _revision) RefreshLabels();
        }
    }

    private async void OnToggled(object sender, RoutedEventArgs args)
    {
        if (_syncing || _disposed || _writing || !_enabled) return;
        var enabled = _toggle.IsOn;
        _writing = true;
        _revision++;
        _toggle.IsEnabled = false;
        try
        {
            await _run(async () =>
            {
                await Task.Run(() => _preferences.SetEnabled(enabled));
            }, "ErrorDoNotDisturbSettings");
        }
        finally
        {
            _writing = false;
            // Discard an older in-flight read, then force a read-back after success or failure.
            // RefreshAsync is retried by the page timer if that read has not completed yet.
            if (!_disposed) { RefreshLabels(); await RefreshAsync(); }
        }
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        OpenSettings.IsEnabled = enabled;
        _toggle.IsEnabled = enabled && !_writing && _state != DoNotDisturbState.Unavailable;
    }

    private void RefreshLabels()
    {
        _syncing = true;
        try { if (!_writing) _toggle.IsOn = _state == DoNotDisturbState.On; }
        finally { _syncing = false; }
        _row.Status = Localization.Get(_state switch
        {
            DoNotDisturbState.On => "On", DoNotDisturbState.Off => "Off", _ => "Unavailable"
        });
        _row.Tone = _state == DoNotDisturbState.On ? StatusTone.Success : StatusTone.Neutral;
        _row.Detail = _state == DoNotDisturbState.Unavailable ? Localization.Get("DoNotDisturbOpenSettings") : "";
        ToolTipService.SetToolTip(_toggle, Localization.Get("SystemDoNotDisturb"));
        SetEnabled(_enabled);
    }

    public void Dispose()
    {
        _disposed = true;
        _toggle.Toggled -= OnToggled;
    }
}
