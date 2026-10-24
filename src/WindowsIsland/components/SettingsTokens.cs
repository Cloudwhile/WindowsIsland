using Microsoft.UI.Xaml;

namespace WindowsIsland.Components;

internal static class SettingsTokens
{
    public const double PagePadding = 24, PageTitleSize = 26, SectionTitleSize = 14;
    public const double BodyTextSize = 14, DescriptionTextSize = 12, SectionSpacing = 24;
    public const double RowMinHeight = 56, RowPadding = 12, ControlSpacing = 8;
    public const double GroupRadius = 8, IconSize = 20, IconColumnWidth = 24, NavigationWidth = 200;
    public static Thickness PageMargin => new(PagePadding, 0, PagePadding, PagePadding);
    public static Thickness DetailMargin => new(12, 8, 12, 16);
}
