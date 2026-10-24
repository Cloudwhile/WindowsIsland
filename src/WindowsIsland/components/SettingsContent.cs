using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace WindowsIsland.Components;

/// <summary>设置详情及说明的统一容器，缩进与展开内容一致。</summary>
internal class SettingsContent : StackPanel
{
    public SettingsContent(params UIElement[] children) : this(true, children) { }

    public SettingsContent(bool inset, params UIElement[] children)
    {
        Spacing = SettingsTokens.RowPadding;
        if (inset) Margin = SettingsTokens.DetailMargin;
        foreach (var child in children) Children.Add(child);
    }

    /// <summary>包装可独立显示隐藏的说明，收起时不留下空白边距。</summary>
    public static SettingsContent Wrap(FrameworkElement child)
    {
        var content = new SettingsContent(child);
        content.SetBinding(VisibilityProperty, new Binding { Source = child, Path = new PropertyPath(nameof(Visibility)), Mode = BindingMode.OneWay });
        return content;
    }
}
