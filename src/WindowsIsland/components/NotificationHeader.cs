using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class NotificationHeader : Grid
{
    private readonly AppIcon _icon = new(14) { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBlock _app = IslandTheme.Text("", 12);
    private readonly TextBlock _separator = IslandTheme.Text(" · ", 12, secondary: true);
    private readonly TextBlock _sender = IslandTheme.Text("", 14);
    private readonly TextBlock _time = IslandTheme.Text("", 12, secondary: true);

    public NotificationHeader()
    {
        Height = NotificationLayout.HeaderHeight;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _app.FontWeight = _sender.FontWeight = FontWeights.SemiBold;
        _time.Margin = new Thickness(12, 0, 0, 0);
        _time.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_app, 1);
        Grid.SetColumn(_separator, 2);
        Grid.SetColumn(_sender, 3);
        Grid.SetColumn(_time, 4);
        Children.Add(_icon); Children.Add(_app); Children.Add(_separator); Children.Add(_sender); Children.Add(_time);
        AutomationProperties.SetAutomationId(_time, "NotificationTime");
        AutomationProperties.SetAutomationId(_app, "NotificationAppName");
        AutomationProperties.SetAutomationId(_sender, "NotificationTitle");
    }

    public void Show(IslandNotification notification)
    {
        _app.Text = notification.AppName;
        _app.Visibility = Visibility.Visible;
        _app.MaxWidth = 112;
        _sender.Text = notification.Title;
        var hasAvatar = notification.SenderAvatar is { Length: > 0 };
        _icon.Visibility = hasAvatar ? Visibility.Visible : Visibility.Collapsed;
        if (hasAvatar) _ = _icon.ShowAsync(notification.AppName, notification.AppIcon, notification.Symbol);
        else _icon.Clear();
        _separator.Visibility = string.IsNullOrWhiteSpace(notification.Title) ? Visibility.Collapsed : Visibility.Visible;
        var age = DateTimeOffset.Now - notification.CreatedAt;
        _time.Text = age.TotalSeconds < 60 ? "现在" : notification.CreatedAt.ToLocalTime().ToString("HH:mm");
    }

    public double NaturalWidth()
    {
        var size = new Size(double.PositiveInfinity, double.PositiveInfinity);
        _icon.Measure(size);
        foreach (var text in new[] { _app, _separator, _sender, _time }) text.Measure(size);
        return _icon.DesiredSize.Width + _app.DesiredSize.Width + _separator.DesiredSize.Width
            + _sender.DesiredSize.Width + _time.DesiredSize.Width;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var natural = new Size(double.PositiveInfinity, double.PositiveInfinity);
        _time.Measure(natural);
        _icon.Measure(natural);
        _sender.Measure(natural);
        var hasSender = !string.IsNullOrWhiteSpace(_sender.Text);
        var nameSpace = Math.Max(0, availableSize.Width - _time.DesiredSize.Width - _icon.DesiredSize.Width);
        var showAppName = !hasSender || nameSpace >= 72;
        _app.Visibility = showAppName ? Visibility.Visible : Visibility.Collapsed;
        _separator.Visibility = showAppName && hasSender ? Visibility.Visible : Visibility.Collapsed;
        _separator.Measure(natural);
        var identityWidth = Math.Max(0, nameSpace - _separator.DesiredSize.Width);
        var senderWidth = Math.Min(_sender.DesiredSize.Width, identityWidth * 0.6);
        _app.MaxWidth = Math.Min(112, Math.Max(0, identityWidth - senderWidth));
        return base.MeasureOverride(availableSize);
    }

    public void Clear()
    {
        _app.Text = _sender.Text = _time.Text = "";
        _separator.Visibility = _icon.Visibility = Visibility.Collapsed;
        _icon.Clear();
    }
}
