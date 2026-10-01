using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class NotificationPanel : StackPanel
{
    private readonly TextBlock _app = IslandTheme.Text("", 12, IslandTheme.Blue);
    private readonly TextBlock _title = IslandTheme.Text("", 20);
    private readonly TextBlock _body = IslandTheme.Text("", 14, IslandTheme.Muted);

    public NotificationPanel()
    {
        Spacing = 10;
        var heading = new Grid { ColumnSpacing = 10 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.Children.Add(IslandTheme.Icon("\uE7E7", 18, IslandTheme.Blue));
        Grid.SetColumn(_app, 1); heading.Children.Add(_app);
        _title.MaxLines = 2; _title.TextWrapping = TextWrapping.Wrap;
        _body.MaxLines = 3; _body.TextWrapping = TextWrapping.Wrap;
        Children.Add(heading); Children.Add(_title); Children.Add(_body);
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
    }

    public void Show(IslandNotification notification)
    {
        _app.Text = notification.AppName;
        _title.Text = notification.Title;
        _body.Text = notification.Body;
        AutomationProperties.SetName(this, $"{notification.AppName}，{notification.Title}，{notification.Body}");
        FrameworkElementAutomationPeer.FromElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    public void Clear()
    {
        _app.Text = _title.Text = _body.Text = "";
        AutomationProperties.SetName(this, "");
    }
}
