using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WindowsIsland.Components;

internal sealed class SettingsSection : StackPanel
{
    public SettingsSection(string title, params UIElement[] rows) : this(title, true, rows) { }

    public SettingsSection(string title, bool localizeTitle, params UIElement[] rows)
    {
        Spacing = 0;
        var heading = localizeTitle ? LocalizedUI.Text(title, 14, secondary: true) : IslandTheme.Text(title, 14, secondary: true);
        heading.TextWrapping = TextWrapping.Wrap;
        heading.FontWeight = FontWeights.SemiBold;
        heading.Margin = new Thickness(12, 16, 0, 4);
        Children.Add(heading);
        foreach (var row in rows) Children.Add(row);
    }
}
