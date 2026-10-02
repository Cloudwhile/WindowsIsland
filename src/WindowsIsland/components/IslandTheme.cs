using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace WindowsIsland.Components;

internal static class IslandTheme
{
    public static TextBlock Text(string value, double size = 14, bool secondary = false)
    {
        var brush = secondary ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush";
        var text = (TextBlock)XamlReader.Load($"<TextBlock xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Foreground='{{ThemeResource {brush}}}' />");
        text.Text = value;
        text.FontSize = size;
        text.VerticalAlignment = VerticalAlignment.Center;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        return text;
    }
}
