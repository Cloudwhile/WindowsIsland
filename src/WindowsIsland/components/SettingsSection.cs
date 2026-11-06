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
        foreach (var child in panel.Children.OfType<Panel>())
            if (RowHeader(child) is null) HideLastSeparator(child);
        var last = panel.Children.OfType<FrameworkElement>().LastOrDefault(e => e.Visibility == Visibility.Visible);
        if (last is not null && RowHeader(last) is { } row) row.ShowSeparator = false;
    }

    /// <summary>仅在局部列表结构变化时重置分隔线，恢复原末行并收起新末行。</summary>
    public static void UpdateSeparators(Panel panel)
    {
        var rows = panel.Children.OfType<FrameworkElement>().Where(child => child.Visibility == Visibility.Visible)
            .Select(RowHeader)
            .OfType<SettingsRow>().ToArray();
        for (var index = 0; index < rows.Length; index++) rows[index].ShowSeparator = index < rows.Length - 1;
    }

    private static SettingsRow? RowHeader(FrameworkElement element) => element switch
    {
        SettingsRow row => row,
        SettingsLinkRow link => link.Header,
        SettingsExpander expander => expander.Header,
        SettingsListItem item => item.Header,
        _ => null
    };
}

/// <summary>将链接行与附属提示视为同一逻辑行，避免分别推断分隔线边界。</summary>
internal sealed class SettingsListItem : StackPanel
{
    public SettingsRow Header { get; }

    public SettingsListItem(SettingsLinkRow row, UIElement notice)
    {
        Header = row.Header;
        Children.Add(row);
        Children.Add(notice);
    }
}
