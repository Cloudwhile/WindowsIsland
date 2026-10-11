using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class IconActionButton : Button
{
    private int? _glyph;
    private string? _labelKey;

    public IconActionButton(Symbol symbol, string label, string automationId) : this((int)symbol, label, automationId) { }

    // Segoe Fluent glyphs not exposed by Symbol, such as chevrons and the palette.
    public IconActionButton(int glyph, string label, string automationId)
    {
        Width = Height = 36;
        Padding = new Thickness(0);
        CornerRadius = new CornerRadius(6);
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Right;
        AutomationProperties.SetAutomationId(this, automationId);
        SetAction(glyph, label);
        LocalizedUI.Bind(this, RefreshLabel);
    }

    public void SetAction(Symbol symbol, string label) => SetAction((int)symbol, label);

    public void SetAction(int glyph, string label)
    {
        if (_glyph == glyph && _labelKey == label) return;
        _glyph = glyph;
        _labelKey = label;
        Content = new FontIcon { Glyph = char.ConvertFromUtf32(glyph), FontSize = 16 };
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
