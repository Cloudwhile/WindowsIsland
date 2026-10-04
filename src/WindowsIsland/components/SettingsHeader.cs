using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace WindowsIsland.Components;

internal sealed class SettingsHeader : Grid
{
    private readonly TextBlock _status = IslandTheme.Text("", 12, secondary: true);
    public string Status { get => _status.Text; set => _status.Text = value; }

    public SettingsHeader(params UIElement[] actions)
    {
        Margin = new Thickness(24, 12, 24, 16);
        ColumnSpacing = 16;
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = IslandTheme.Text("设置", 28);
        title.FontWeight = FontWeights.SemiBold;
        var labels = new StackPanel { Spacing = 4 };
        labels.Children.Add(title);
        labels.Children.Add(_status);
        Children.Add(labels);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        foreach (var action in actions) toolbar.Children.Add(action);
        Grid.SetColumn(toolbar, 1);
        Children.Add(toolbar);
        AutomationProperties.SetAutomationId(_status, "SettingsReadiness");
    }
}
