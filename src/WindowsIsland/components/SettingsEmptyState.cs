using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WindowsIsland.Components;

/// <summary>设置列表共用的主题空态，不引入额外卡片层级。</summary>
internal sealed class SettingsEmptyState : SettingsContent
{
    public SettingsEmptyState(Symbol icon, string title, string? description = null) : base(false)
    {
        Padding = new Thickness(0, SettingsTokens.ControlSpacing, 0, SettingsTokens.ControlSpacing);
        Children.Add(new SymbolIcon(icon)
        {
            Width = SettingsTokens.IconSize,
            Height = SettingsTokens.IconSize,
            Opacity = 0.72,
            HorizontalAlignment = HorizontalAlignment.Left
        });
        Children.Add(SettingsSection.Heading(title));
        if (description is not null)
        {
            var detail = LocalizedUI.Text(description, SettingsTokens.DescriptionTextSize, secondary: true);
            detail.TextWrapping = TextWrapping.Wrap;
            Children.Add(detail);
        }
    }
}
