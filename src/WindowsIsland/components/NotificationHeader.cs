using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class NotificationHeader : Grid
{
    private readonly Grid _iconSlot = new() { Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) };
    private readonly AppIcon _icon = new(16, "NotificationHeaderIcon");
    private readonly PowerBatteryIcon _battery = new(16, "") { Visibility = Visibility.Collapsed };
    private readonly TextBlock _app = IslandTheme.Text("", 12, secondary: true);
    private readonly TextBlock _time = IslandTheme.Text("", 12, secondary: true);
    private IslandNotification? _notification;

    public NotificationHeader()
    {
        Height = NotificationLayout.HeaderHeight;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _iconSlot.Children.Add(_icon);
        _iconSlot.Children.Add(_battery);
        _time.Margin = new Thickness(16, 0, 0, 0);
        _time.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_app, 1);
        Grid.SetColumn(_time, 2);
        Children.Add(_iconSlot); Children.Add(_app); Children.Add(_time);
        AutomationProperties.SetAutomationId(_time, "NotificationTime");
        AutomationProperties.SetAutomationId(_app, "NotificationAppName");
        LocalizedUI.Bind(this, RefreshText);
    }

    public void Show(IslandNotification notification)
    {
        _notification = notification;
        if (PowerEvents.TryResolveVisual(notification, out var state))
        {
            _icon.Clear();
            _icon.Visibility = Visibility.Collapsed;
            _icon.SetAutomationId("");
            _battery.SetAutomationId("NotificationHeaderIcon");
            _battery.Visibility = Visibility.Visible;
            _battery.Show(Localization.Get("Power"), state);
        }
        else
        {
            _battery.Clear();
            _battery.Visibility = Visibility.Collapsed;
            _battery.SetAutomationId("");
            _icon.SetAutomationId("NotificationHeaderIcon");
            _icon.Visibility = Visibility.Visible;
            _ = _icon.ShowAsync(notification.AppName, notification.AppIcon, notification.Symbol);
        }
        RefreshText();
    }

    private void RefreshText()
    {
        if (_notification is not { } notification) return;
        var appName = notification.Source == NotificationSource.Power ? Localization.Get("Power") : notification.AppName;
        _app.Text = appName;
        if (_battery.Visibility == Visibility.Visible) AutomationProperties.SetName(_battery, appName);
        var age = DateTimeOffset.Now - notification.CreatedAt;
        _time.Text = age.TotalSeconds < 60 ? Localization.Get("NotificationNow") : notification.CreatedAt.ToLocalTime().ToString("HH:mm", Localization.Culture);
    }

    public double NaturalWidth()
    {
        var size = new Size(double.PositiveInfinity, double.PositiveInfinity);
        _iconSlot.Measure(size); _app.Measure(size); _time.Measure(size);
        return _iconSlot.DesiredSize.Width + _app.DesiredSize.Width + _time.DesiredSize.Width;
    }

    public void Clear()
    {
        _notification = null;
        _app.Text = _time.Text = "";
        _icon.Clear();
        _battery.Clear();
        _battery.Visibility = Visibility.Collapsed;
    }
}
