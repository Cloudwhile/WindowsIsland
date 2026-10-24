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
        heading.Margin = new Thickness(SettingsTokens.RowPadding, SettingsTokens.SectionSpacing, 0, SettingsTokens.ControlSpacing);
        Children.Add(heading);
        foreach (var row in rows) Children.Add(row);
    }

    public static TextBlock Heading(string title, bool localizeTitle = true)
    {
        var heading = localizeTitle ? LocalizedUI.Text(title, SettingsTokens.SectionTitleSize, secondary: true) : IslandTheme.Text(title, SettingsTokens.SectionTitleSize, secondary: true);
        heading.TextWrapping = TextWrapping.Wrap;
        heading.FontWeight = FontWeights.SemiBold;
        return heading;
    }

    public static StackPanel Page(params UIElement[] rows)
    {
        var content = new StackPanel { Margin = SettingsTokens.PageMargin };
        foreach (var row in rows) content.Children.Add(row);
        content.Loaded += (_, _) => HideLastSeparator(content);
        return content;
    }

    private static void HideLastSeparator(Panel panel)
    {
        foreach (var child in panel.Children.OfType<Panel>()) HideLastSeparator(child);
        var last = panel.Children.OfType<FrameworkElement>().LastOrDefault(e => e.Visibility == Visibility.Visible);
        if (last is SettingsRow row) row.ShowSeparator = false;
        else if (last is SettingsLinkRow link) link.Header.ShowSeparator = false;
    }
}
