using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class NotificationBannerSettings : Grid, IDisposable
{
    private readonly WindowsSettingsService _service;
    private readonly Func<Func<Task>, string, Task> _run;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _status = IslandTheme.Text("", 12, secondary: true);
    private readonly Dictionary<string, (SettingsRow Row, ToggleSwitch Toggle, string Name, bool Available)> _applications = [];
    private bool _syncing, _enabled = true, _reading, _disposed;
    private int _revision;
    private bool _readFailed;
    public IconActionButton OpenSettings { get; } = new(Symbol.Setting, "ActionOpenWindowsNotificationSettings", "OpenSystemNotificationSettings");

    public NotificationBannerSettings(WindowsSettingsService service, Func<Func<Task>, string, Task> run)
    {
        _service = service;
        _run = run;
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new SymbolIcon(Symbol.Message));
        var title = LocalizedUI.Text("HideSystemBanners");
        title.TextWrapping = TextWrapping.Wrap;
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(_status);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(OpenSettings, 2);
        header.Children.Add(text);
        header.Children.Add(OpenSettings);
        var expander = new Expander
        {
            Header = header, Content = _rows, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(expander, "NotificationBannerSettings");
        AutomationProperties.SetAutomationId(_status, "NotificationBannerStatus");
        LocalizedUI.Label(expander, "HideSystemBanners");
        Children.Add(expander);
        LocalizedUI.Bind(this, RefreshLabels);
    }

    public async Task RefreshAsync()
    {
        if (_disposed || _reading || !_enabled) return;
        _reading = true;
        var revision = _revision;
        try
        {
            var apps = await Task.Run(() => _service.Banners.Read()
                .Select(item => (Preference: item, Name: _service.SenderName(item.Identity))).ToArray());
            if (_disposed || !_enabled || revision != _revision) return;
            _readFailed = false;
            _syncing = true;
            foreach (var identity in _applications.Keys.Where(id => !apps.Any(app => app.Preference.Identity == id)).ToArray())
            {
                _rows.Children.Remove(_applications[identity].Row);
                _applications.Remove(identity);
            }
            foreach (var (preference, name) in apps.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (!_applications.TryGetValue(preference.Identity, out var entry))
                {
                    var identity = preference.Identity;
                    var toggle = new ToggleSwitch { OnContent = null, OffContent = null, MinWidth = 0,
                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                    AutomationProperties.SetAutomationId(toggle, "BannerToggle." + identity);
                    var row = new SettingsRow(Symbol.Message, name, toggle, "BannerState." + identity, localizeTitle: false);
                    entry = (row, toggle, name, preference.Available);
                    _applications[identity] = entry;
                    _rows.Children.Add(row);
                    toggle.Toggled += async (_, _) =>
                    {
                        if (_syncing || _disposed) return;
                        _revision++;
                        var hidden = toggle.IsOn;
                        await _run(async () =>
                        {
                            await Task.Run(() => _service.Banners.SetHidden(identity, hidden));
                        }, "ErrorBannerSettings");
                        await RefreshAsync();
                    };
                }
                entry.Toggle.IsOn = preference.Hidden;
                entry.Toggle.IsEnabled = preference.Available;
                _applications[preference.Identity] = (entry.Row, entry.Toggle, entry.Name, preference.Available);
                entry.Row.Status = Localization.Get(!preference.Available ? "Unavailable" : preference.Hidden ? "BannersHidden" : "BannersShown");
            }
            _status.Text = apps.Length == 0 ? Localization.Get("NoNotificationApps") : Localization.Format("BannerApplications", apps.Length);
            RefreshLabels();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (!_disposed) { _readFailed = true; _status.Text = Localization.Get("ErrorReadWindowsSettings"); _rows.Children.Clear(); _applications.Clear(); }
        }
        finally { _syncing = false; _reading = false; }
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        OpenSettings.IsEnabled = enabled;
        foreach (var entry in _applications.Values) entry.Toggle.IsEnabled = enabled && entry.Available;
    }

    private void RefreshLabels()
    {
        _status.Text = _readFailed ? Localization.Get("ErrorReadWindowsSettings") : _applications.Count == 0
            ? Localization.Get("NoNotificationApps") : Localization.Format("BannerApplications", _applications.Count);
        foreach (var entry in _applications.Values)
        {
            entry.Row.Status = Localization.Get(!entry.Available ? "Unavailable" : entry.Toggle.IsOn ? "BannersHidden" : "BannersShown");
            AutomationProperties.SetName(entry.Toggle, Localization.Format("ActionHideAppBanners", entry.Name));
            ToolTipService.SetToolTip(entry.Toggle, Localization.Format("ActionHideAppBanners", entry.Name));
        }
    }

    public void Dispose() => _disposed = true;
}
