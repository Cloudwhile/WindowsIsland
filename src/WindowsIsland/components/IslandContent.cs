using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WindowsIsland.Contracts;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

// 三态共用一个 IslandSurface，侧边表示留出独立列，不覆盖消息。
internal sealed class IslandContent : Grid
{
    public const double CompactWidth = 156, CompactHeight = 42, PinnedInset = 24;
    private readonly NotificationPanel _message;
    public PinnedPanel Panel { get; } = new() { Visibility = Visibility.Collapsed };
    private readonly Grid _compact = new() { ColumnSpacing = 10 };
    private readonly TextBlock _icon = IslandTheme.Text("", 18);
    private readonly TextBlock _value = IslandTheme.Text("", 18);
    private readonly TextBlock _left = IslandTheme.Text("", 12);
    private readonly TextBlock _right = IslandTheme.Text("●", 8);

    public IslandContent(NotificationPanel message)
    {
        _message = message;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _compact.ColumnDefinitions.Add(new ColumnDefinition());
        _icon.FontFamily = _left.FontFamily = new FontFamily("Segoe Fluent Icons");
        _value.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        Grid.SetColumn(_value, 1);
        _compact.Children.Add(_icon);
        _compact.Children.Add(_value);
        Grid.SetColumn(_message, 1);
        Grid.SetColumn(_compact, 1);
        Grid.SetColumn(Panel, 1);
        Grid.SetColumn(_right, 2);
        _left.Width = _right.Width = PinnedInset;
        _left.TextAlignment = _right.TextAlignment = TextAlignment.Center;
        Children.Add(_left); Children.Add(_message); Children.Add(_compact); Children.Add(_right); Children.Add(Panel);
        AutomationProperties.SetAutomationId(_compact, "PinnedCompact");
        AutomationProperties.SetAutomationId(_value, "PinnedCompactText");
        AutomationProperties.SetAutomationId(_left, "PinnedMinimalIcon");
    }

    public void Present(IslandState state, PinnedContent? pinned, IslandExpandedKind expanded = IslandExpandedKind.Message,
        NotificationPosition position = NotificationPosition.TopCenter)
    {
        var compact = state == IslandState.Compact;
        var message = state == IslandState.Expanded && expanded == IslandExpandedKind.Message;
        var details = state == IslandState.Expanded && expanded == IslandExpandedKind.Pinned && pinned is not null;
        var markers = message && pinned is not null;
        _compact.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        _message.Visibility = message ? Visibility.Visible : Visibility.Collapsed;
        Panel.Visibility = details ? Visibility.Visible : Visibility.Collapsed;
        if (details) Panel.Present(pinned!, position);
        _left.Visibility = _right.Visibility = markers ? Visibility.Visible : Visibility.Collapsed;
        _icon.Text = _left.Text = pinned?.Symbol == "timer" ? "\uE916" : "\uE718";
        _value.Text = pinned?.CompactText.ReplaceLineEndings(" ") ?? "";
        var label = pinned is null ? "" : $"{pinned.Title}，{pinned.CompactText}";
        AutomationProperties.SetName(_compact, label);
        AutomationProperties.SetName(_left, label);
        ToolTipService.SetToolTip(_compact, pinned?.Body ?? pinned?.Title);
        ToolTipService.SetToolTip(_left, label);
    }
    public void ClearPanel() => Panel.Visibility = Visibility.Collapsed;

    public void SetContentWidth(double width, IslandState state, bool pinned)
    {
        Width = Math.Max(1, width);
        _message.Width = Math.Max(1, width - (state == IslandState.Expanded && pinned ? PinnedInset * 2 : 0));
    }
}
