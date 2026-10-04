using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace WindowsIsland.Components;

internal sealed class NotificationBannerGuide : Grid
{
    public IconActionButton OpenSettings { get; } = new(Symbol.Setting, "打开 Windows 通知设置", "OpenSystemNotificationSettings");

    public NotificationBannerGuide()
    {
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.Children.Add(new SymbolIcon(Symbol.Message));
        var title = IslandTheme.Text("仅使用消息岛通知");
        title.TextWrapping = TextWrapping.Wrap;
        SetColumn(title, 1);
        header.Children.Add(title);
        var steps = new StackPanel { Spacing = 12 };
        foreach (var text in new[]
        {
            "1. 打开 Windows 通知设置，选择需要接管的应用。",
            "2. 关闭“显示通知横幅”，保留该应用的“通知”和“在通知中心显示通知”。",
            "3. 回到消息岛，保持下方“系统通知”来源开启。"
        })
        {
            var line = IslandTheme.Text(text, 14);
            line.TextWrapping = TextWrapping.Wrap;
            steps.Children.Add(line);
        }
        steps.Children.Add(OpenSettings);
        var expander = new Expander
        {
            Header = header, Content = steps, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(expander, "NotificationBannerGuide");
        AutomationProperties.SetName(expander, "仅使用消息岛通知");
        Children.Add(expander);
    }
}
