using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace WindowsIsland.Components;

internal sealed class SettingsSetup : Grid
{
    private readonly Expander _expander = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBlock _status = IslandTheme.Text("", 12, secondary: true);
    public string Status { get => _status.Text; set => _status.Text = value; }
    public bool IsExpanded { get => _expander.IsExpanded; set => _expander.IsExpanded = value; }

    public SettingsSetup(params UIElement[] rows)
    {
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new SymbolIcon(Symbol.Permissions));
        var title = IslandTheme.Text("初始化");
        title.FontWeight = FontWeights.SemiBold;
        Grid.SetColumn(title, 1);
        Grid.SetColumn(_status, 2);
        header.Children.Add(title);
        header.Children.Add(_status);
        _expander.Header = header;
        var content = new StackPanel();
        foreach (var row in rows) content.Children.Add(row);
        _expander.Content = content;
        Children.Add(_expander);
        AutomationProperties.SetAutomationId(_expander, "InitializationSection");
        AutomationProperties.SetName(_expander, "初始化");
    }
}
