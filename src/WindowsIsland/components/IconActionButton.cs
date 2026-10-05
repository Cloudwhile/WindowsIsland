using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class IconActionButton : Button
{
    private Symbol? _symbol;
    private string? _labelKey;

    public IconActionButton(Symbol symbol, string label, string automationId)
    {
        Width = Height = 36;
        Padding = new Thickness(0);
        CornerRadius = new CornerRadius(6);
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Right;
        AutomationProperties.SetAutomationId(this, automationId);
        SetAction(symbol, label);
        LocalizedUI.Bind(this, RefreshLabel);
    }

    public void SetAction(Symbol symbol, string label)
    {
        if (_symbol == symbol && _labelKey == label) return;
        _symbol = symbol;
        _labelKey = label;
        Content = new FontIcon { Glyph = char.ConvertFromUtf32((int)symbol), FontSize = 16 };
        RefreshLabel();
    }

    private void RefreshLabel()
    {
        if (_labelKey is null) return;
        var label = Localization.Get(_labelKey);
        AutomationProperties.SetName(this, label);
        ToolTipService.SetToolTip(this, label);
    }
}
