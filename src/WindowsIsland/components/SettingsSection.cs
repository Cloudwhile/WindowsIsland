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
        var heading = Heading(title, localizeTitle);
        heading.Margin = new Thickness(12, 16, 0, 4);
        Children.Add(heading);
        foreach (var row in rows) Children.Add(row);
    }

    public static TextBlock Heading(string title, bool localizeTitle = true)
    {
        var heading = localizeTitle ? LocalizedUI.Text(title, 14, secondary: true) : IslandTheme.Text(title, 14, secondary: true);
        heading.TextWrapping = TextWrapping.Wrap;
        heading.FontWeight = FontWeights.SemiBold;
        return heading;
    }

    public static StackPanel Page(params UIElement[] rows)
    {
        var content = new StackPanel { Margin = new Thickness(16, 0, 16, 24) };
        foreach (var row in rows) content.Children.Add(row);
        return content;
    }
}
