using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace WindowsIsland.Components;

internal static class IslandTheme
{
    public static readonly SolidColorBrush White = Brush(242, 244, 248);
    public static readonly SolidColorBrush Muted = Brush(149, 153, 164);
    public static readonly SolidColorBrush Blue = Brush(133, 181, 255);
    public static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromArgb(255, r, g, b));
    public static TextBlock Text(string value, double size = 14, SolidColorBrush? color = null) => new()
    {
        Text = value, FontSize = size, Foreground = color ?? White,
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
    };
    public static FontIcon Icon(string glyph, double size = 18, SolidColorBrush? color = null) => new()
    {
        Glyph = glyph, FontSize = size, Foreground = color ?? White,
        FontFamily = new FontFamily("Segoe Fluent Icons")
    };
}
