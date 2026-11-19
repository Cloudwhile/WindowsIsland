using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace WindowsIsland.Components;

internal sealed class SettingsSummaryTile : Grid
{
    private readonly TextBlock _value = IslandTheme.Text("", SettingsTokens.PageTitleSize);
    private readonly TextBlock _detail = IslandTheme.Text("", SettingsTokens.DescriptionTextSize, secondary: true);
    public string Value { set { if (_value.Text != value) _value.Text = value; } }
    public string Detail { set { if (_detail.Text != value) _detail.Text = value; } }

    public SettingsSummaryTile(Symbol icon, string caption, string id)
    {
        var surface = (Border)XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource CardBackgroundFillColorDefaultBrush}' BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}' BorderThickness='1' />");
        surface.CornerRadius = new CornerRadius(SettingsTokens.GroupRadius);
        surface.Padding = new Thickness(16, 14, 16, 14);
        var header = new Grid { ColumnSpacing = SettingsTokens.ControlSpacing };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        var glyph = (FontIcon)XamlReader.Load("<FontIcon xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' FontSize='16' Foreground='{ThemeResource TextFillColorSecondaryBrush}' />");
        glyph.Glyph = char.ConvertFromUtf32((int)icon);
        glyph.VerticalAlignment = VerticalAlignment.Center;
        var label = LocalizedUI.Text(caption, SettingsTokens.DescriptionTextSize, secondary: true);
        label.FontWeight = FontWeights.SemiBold;
        label.TextWrapping = TextWrapping.Wrap;
        Grid.SetColumn(label, 1);
        header.Children.Add(glyph);
        header.Children.Add(label);
        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(header);
        _value.FontWeight = FontWeights.SemiBold;
        _value.TextWrapping = _detail.TextWrapping = TextWrapping.Wrap;
        content.Children.Add(_value);
        content.Children.Add(_detail);
        surface.Child = content;
        Children.Add(surface);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_value, id);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_detail, id + ".Detail");
    }
}
