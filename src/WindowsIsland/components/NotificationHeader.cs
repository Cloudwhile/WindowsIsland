using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class NotificationHeader : Grid
{
    private readonly AppIcon _icon = new(16) { Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _app = IslandTheme.Text("", 12, secondary: true);
    private readonly TextBlock _time = IslandTheme.Text("", 12, secondary: true);

    public NotificationHeader()
    {
        Height = NotificationLayout.HeaderHeight;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _time.Margin = new Thickness(16, 0, 0, 0);
        _time.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_app, 1);
        Grid.SetColumn(_time, 2);
        Children.Add(_icon); Children.Add(_app); Children.Add(_time);
        AutomationProperties.SetAutomationId(_icon, "NotificationHeaderIcon");
        AutomationProperties.SetAutomationId(_time, "NotificationTime");
        AutomationProperties.SetAutomationId(_app, "NotificationAppName");
    }

    public void Show(IslandNotification notification)
    {
        _app.Text = notification.AppName;
        _ = _icon.ShowAsync(notification.AppName, notification.AppIcon, notification.Symbol);
        var age = DateTimeOffset.Now - notification.CreatedAt;
        _time.Text = age.TotalSeconds < 60 ? "现在" : notification.CreatedAt.ToLocalTime().ToString("HH:mm");
    }

    public double NaturalWidth()
    {
        var size = new Size(double.PositiveInfinity, double.PositiveInfinity);
        _icon.Measure(size); _app.Measure(size); _time.Measure(size);
        return _icon.DesiredSize.Width + _app.DesiredSize.Width + _time.DesiredSize.Width;
    }

    public void Clear()
    {
        _app.Text = _time.Text = "";
        _icon.Clear();
    }
}
