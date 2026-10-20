using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace WindowsIsland.Components;

// A list row with a trailing chevron. Child rows are indented in the same flat list
// instead of a native Expander card, so expandable rows look alike on every page.
internal sealed class SettingsExpander : StackPanel
{
    private const int ExpandGlyph = 0xE70D, CollapseGlyph = 0xE70E;
    private readonly IconActionButton _toggle;
    private readonly StackPanel _content = new() { Margin = new Thickness(40, 0, 0, 0), Visibility = Visibility.Collapsed };
    public SettingsRow Header { get; }
    public UIElementCollection Rows => _content.Children;
    public event Action<bool>? ExpandedChanged;
    /// <summary>是否允许点击标题区域展开详情，操作控件保持独立响应。</summary>
    public bool ExpandOnHeaderTap { get; set; }

    public bool IsExpanded
    {
        get => _content.Visibility == Visibility.Visible;
        set
        {
            if (IsExpanded == value) return;
            _content.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            _toggle.SetAction(value ? CollapseGlyph : ExpandGlyph, value ? "ActionCollapse" : "ActionExpand");
            ExpandedChanged?.Invoke(value);
        }
    }

    public SettingsExpander(IconElement icon, string title, string automationId, string? statusId = null,
        bool localizeTitle = true, params FrameworkElement[] actions)
    {
        _toggle = new IconActionButton(ExpandGlyph, "ActionExpand", automationId + ".Toggle");
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (var action in actions) toolbar.Children.Add(action);
        toolbar.Children.Add(_toggle);
        Header = new SettingsRow(icon, title, toolbar, statusId, localizeTitle);
        Header.Tapped += (_, args) =>
        {
            if (!ExpandOnHeaderTap) return;
            for (var element = args.OriginalSource as DependencyObject; element is not null && element != Header;
                element = VisualTreeHelper.GetParent(element))
                if (element is ButtonBase or ToggleSwitch) return;
            IsExpanded = !IsExpanded;
            args.Handled = true;
        };
        Children.Add(Header);
        Children.Add(_content);
        _toggle.Click += (_, _) => IsExpanded = !IsExpanded;
        AutomationProperties.SetAutomationId(this, automationId);
        if (localizeTitle) LocalizedUI.Bind(this, () => AutomationProperties.SetName(this, Services.Localization.Get(title)));
    }
}
