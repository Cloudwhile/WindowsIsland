using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace WindowsIsland.Components;

internal sealed class NotificationBannerGuide : Grid
{
    public IconActionButton OpenSettings { get; } = new(Symbol.Setting, "ActionOpenWindowsNotificationSettings", "OpenSystemNotificationSettings");

    public NotificationBannerGuide()
    {
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.Children.Add(new SymbolIcon(Symbol.Message));
        var title = LocalizedUI.Text("BannerTitle");
        title.TextWrapping = TextWrapping.Wrap;
        SetColumn(title, 1);
        header.Children.Add(title);
        var steps = new StackPanel { Spacing = 12 };
        foreach (var text in new[]
        {
            "BannerStep1", "BannerStep2", "BannerStep3"
        })
        {
            var line = LocalizedUI.Text(text, 14);
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
        LocalizedUI.Label(expander, "BannerTitle");
        Children.Add(expander);
    }
}
