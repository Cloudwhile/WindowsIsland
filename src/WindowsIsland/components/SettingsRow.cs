using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WindowsIsland.Components;

internal sealed class SettingsRow : Grid
{
    private readonly TextBlock _status = IslandTheme.Text("", 12, secondary: true);
    public string Status { get => _status.Text; set => _status.Text = value; }

    public SettingsRow(Symbol symbol, string title, FrameworkElement action)
        : this(new SymbolIcon(symbol), title, action) { }

    public SettingsRow(IconElement icon, string title, FrameworkElement action)
    {
        MinHeight = 68;
        Padding = new Thickness(0, 10, 0, 10);
        ColumnSpacing = 16;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        icon.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(icon);
        var name = IslandTheme.Text(title);
        name.FontWeight = FontWeights.SemiBold;
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(name);
        text.Children.Add(_status);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(action, 2);
        Children.Add(text);
        Children.Add(action);
    }
}
