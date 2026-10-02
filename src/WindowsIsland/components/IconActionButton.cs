using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace WindowsIsland.Components;

internal sealed class IconActionButton : Button
{
    public IconActionButton(Symbol symbol, string label, string automationId)
    {
        Content = new SymbolIcon(symbol);
        VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(this, label);
        AutomationProperties.SetAutomationId(this, automationId);
        ToolTipService.SetToolTip(this, label);
    }

    public void SetAction(Symbol symbol, string label)
    {
        Content = new SymbolIcon(symbol);
        AutomationProperties.SetName(this, label);
        ToolTipService.SetToolTip(this, label);
    }
}
