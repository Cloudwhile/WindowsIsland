using Microsoft.UI.Text;
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
    private readonly TextBlock _title = IslandTheme.Text("");
    private readonly TextBlock _body = IslandTheme.Text("");

    public NotificationPanel()
    {
        VerticalAlignment = VerticalAlignment.Top;
        HorizontalAlignment = HorizontalAlignment.Center;
        UseLayoutRounding = true;
        ColumnSpacing = NotificationLayout.IconSpacing;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(AppIcon.IconSize) });
        ColumnDefinitions.Add(new ColumnDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _text.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _text.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _text.VerticalAlignment = VerticalAlignment.Top;
        _title.FontWeight = FontWeights.SemiBold;
        _title.LineHeight = NotificationLayout.TitleHeight;
        _title.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _title.MaxLines = 1;
        _body.LineHeight = 20;
        _body.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _body.MaxLines = 6; _body.TextWrapping = TextWrapping.Wrap;
        _text.Margin = _icon.Margin = new Thickness(0, NotificationLayout.HeaderSpacing, 0, 0);
        Grid.SetRow(_body, 1);
        _text.Children.Add(_title); _text.Children.Add(_body);
        Grid.SetColumn(_text, 1);
        Grid.SetRow(_icon, 1); Grid.SetRow(_text, 1);
        Grid.SetColumnSpan(_header, 2);
        Children.Add(_header); Children.Add(_icon); Children.Add(_text);
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        AutomationProperties.SetAutomationId(_title, "NotificationTitle");
        AutomationProperties.SetAutomationId(_body, "NotificationBody");
    }

    public void Show(IslandNotification notification)
    {
        _header.Show(notification);
        _title.Text = notification.Title;
        _title.Visibility = string.IsNullOrWhiteSpace(notification.Title) ? Visibility.Collapsed : Visibility.Visible;
        _body.Text = notification.Body;
        _body.Visibility = string.IsNullOrWhiteSpace(notification.Body) ? Visibility.Collapsed : Visibility.Visible;
        _body.Margin = new Thickness(0, _title.Visibility == Visibility.Visible ? NotificationLayout.BodySpacing : 0, 0, 0);
        _icon.Show(notification);
        AutomationProperties.SetName(this, $"{notification.AppName}，{notification.Title}，{notification.Body}");
        FrameworkElementAutomationPeer.FromElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    public Size MeasureWindow(double availableWidth, double availableHeight)
    {
        var natural = new Size(double.PositiveInfinity, double.PositiveInfinity);
        _body.MaxLines = 6;
        _title.Measure(natural); _body.Measure(natural);
        var contentWidth = Math.Max(_title.DesiredSize.Width, _body.DesiredSize.Width);
        var headerWidth = _header.NaturalWidth() - AppIcon.IconSize - NotificationLayout.IconSpacing;
        var width = NotificationLayout.Width(Math.Max(contentWidth, headerWidth) + 4, availableWidth);
        var textSpace = new Size(Math.Max(1, width - NotificationLayout.TextInset), double.PositiveInfinity);
        var titleHeight = _title.Visibility == Visibility.Visible ? NotificationLayout.TitleHeight : 0;
        var bodySpace = Math.Min(NotificationLayout.MaxHeight, availableHeight) - NotificationLayout.VerticalPadding * 2
            - NotificationLayout.HeaderHeight - NotificationLayout.HeaderSpacing - titleHeight - _body.Margin.Top;
        _body.MaxLines = Math.Clamp((int)Math.Floor(bodySpace / _body.LineHeight), 1, 6);
        _body.Measure(textSpace);
        var textHeight = titleHeight + _body.DesiredSize.Height + (_body.Visibility == Visibility.Visible ? _body.Margin.Top : 0);
        return new Size(width, NotificationLayout.Height(textHeight, availableHeight));
    }

    public void Clear()
    {
        _header.Clear();
        _title.Text = _body.Text = "";
        _icon.Clear();
        AutomationProperties.SetName(this, "");
    }
}
