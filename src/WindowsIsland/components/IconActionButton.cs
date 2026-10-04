using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace WindowsIsland.Components;

internal sealed class IconActionButton : Button
{
    private Symbol? _symbol;
    private string? _label;

    public IconActionButton(Symbol symbol, string label, string automationId)
    {
        Width = Height = 36;
        Padding = new Thickness(0);
        CornerRadius = new CornerRadius(6);
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Right;
        AutomationProperties.SetAutomationId(this, automationId);
        SetAction(symbol, label);
    }

    public void SetAction(Symbol symbol, string label)
    {
        if (_symbol == symbol && _label == label) return;
        _symbol = symbol;
        _label = label;
        Content = new FontIcon { Glyph = char.ConvertFromUtf32((int)symbol), FontSize = 16 };
        AutomationProperties.SetName(this, label);
        ToolTipService.SetToolTip(this, label);
    }
}
