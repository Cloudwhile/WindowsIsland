using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WindowsIsland.Components;

internal sealed class SettingsSection : StackPanel
{
    public SettingsSection(string title, params UIElement[] rows)
    {
        Spacing = 0;
        var heading = IslandTheme.Text(title, 14, secondary: true);
        heading.FontWeight = FontWeights.SemiBold;
        heading.Margin = new Thickness(12, 20, 0, 8);
        Children.Add(heading);
        foreach (var row in rows) Children.Add(row);
    }
}
