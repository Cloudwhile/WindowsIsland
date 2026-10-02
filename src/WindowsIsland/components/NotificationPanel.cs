using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class NotificationPanel : Grid
{
    private readonly MessageAvatar _icon = new();
    private readonly Grid _text = new();
    private readonly NotificationHeader _header = new();
    private readonly TextBlock _body = IslandTheme.Text("");

    public NotificationPanel()
    {
        VerticalAlignment = VerticalAlignment.Center;
        ColumnSpacing = NotificationLayout.IconSpacing;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(AppIcon.IconSize) });
        ColumnDefinitions.Add(new ColumnDefinition());
        _text.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _text.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _text.VerticalAlignment = VerticalAlignment.Center;
        _body.LineHeight = 20;
        _body.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _body.Margin = new Thickness(0, NotificationLayout.BodySpacing, 0, 0);
        _body.MaxLines = 6; _body.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(_body, 1);
        _text.Children.Add(_header); _text.Children.Add(_body);
        Grid.SetColumn(_text, 1);
        Children.Add(_icon); Children.Add(_text);
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        AutomationProperties.SetAutomationId(_body, "NotificationBody");
    }

    public void Show(IslandNotification notification)
    {
        _header.Show(notification);
        _body.Text = notification.Body;
        _body.Visibility = string.IsNullOrWhiteSpace(notification.Body) ? Visibility.Collapsed : Visibility.Visible;
        _icon.Show(notification);
        AutomationProperties.SetName(this, $"{notification.AppName}，{notification.Title}，{notification.Body}");
        FrameworkElementAutomationPeer.FromElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    public Size MeasureWindow(double availableWidth, double availableHeight)
    {
        var natural = new Size(double.PositiveInfinity, double.PositiveInfinity);
        _body.MaxLines = 6;
        _body.Measure(natural);
        var width = NotificationLayout.Width(Math.Max(_header.NaturalWidth(), _body.DesiredSize.Width) + 8, availableWidth);
        var textSpace = new Size(Math.Max(1, width - NotificationLayout.TextInset), double.PositiveInfinity);
        var headerHeight = _header.Height;
        var bodySpace = Math.Min(NotificationLayout.MaxHeight, availableHeight)
            - NotificationLayout.VerticalPadding * 2 - headerHeight - _body.Margin.Top;
        _body.MaxLines = Math.Clamp((int)Math.Floor(bodySpace / _body.LineHeight), 1, 6);
        _body.Measure(textSpace);
        return new Size(width, NotificationLayout.Height(headerHeight + _body.DesiredSize.Height, availableHeight));
    }

    public void Clear()
    {
        _header.Clear();
        _body.Text = "";
        _icon.Clear();
        AutomationProperties.SetName(this, "");
    }
}
