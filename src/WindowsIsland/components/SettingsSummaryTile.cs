using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace WindowsIsland.Components;

internal sealed class SettingsSummaryTile : Grid
{
    private readonly TextBlock _value = IslandTheme.Text("", 24);
    private readonly TextBlock _detail = IslandTheme.Text("", SettingsTokens.DescriptionTextSize, secondary: true);
    public string Value { set { if (_value.Text != value) _value.Text = value; } }
    public string Detail { set { if (_detail.Text != value) _detail.Text = value; } }

    public SettingsSummaryTile(Symbol icon, string caption, string id)
    {
        var surface = (Border)XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource CardBackgroundFillColorDefaultBrush}' BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}' BorderThickness='1' />");
        surface.CornerRadius = new CornerRadius(SettingsTokens.GroupRadius);
        surface.Padding = new Thickness(16);
        var content = new StackPanel { Spacing = SettingsTokens.ControlSpacing };
        content.Children.Add(new SymbolIcon(icon) { HorizontalAlignment = HorizontalAlignment.Left });
        _value.FontWeight = FontWeights.SemiBold;
        _value.TextWrapping = _detail.TextWrapping = TextWrapping.Wrap;
        content.Children.Add(_value);
        content.Children.Add(LocalizedUI.Text(caption, SettingsTokens.DescriptionTextSize, secondary: true));
        content.Children.Add(_detail);
        surface.Child = content;
        Children.Add(surface);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_value, id);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_detail, id + ".Detail");
    }
}
