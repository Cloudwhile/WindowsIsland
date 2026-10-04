namespace WindowsIsland.Components;

internal static class NotificationLayout
{
    public const double MaxWidth = 480, MaxHeight = 218;
    public const double MinWidth = 300, MinHeight = 96;
    // Native flyout padding plus its one-pixel border.
    public const double HorizontalPadding = 17, VerticalPadding = 17, IconSpacing = 12;
    public const double HeaderHeight = 20, HeaderSpacing = 8, TitleHeight = 20, BodySpacing = 3, CornerRadius = 28;
    public const double TextInset = HorizontalPadding * 2 + AppIcon.IconSize + IconSpacing;

    public static double Width(double naturalTextWidth, double availableWidth) =>
        Fit(naturalTextWidth + TextInset, MinWidth, MaxWidth, availableWidth);

    public static double Height(double textHeight, double availableHeight) =>
        Fit(Math.Max(textHeight, AppIcon.IconSize) + HeaderHeight + HeaderSpacing + VerticalPadding * 2, MinHeight, MaxHeight, availableHeight);

    private static double Fit(double desired, double minimum, double maximum, double available)
    {
        var limit = Math.Max(1, Math.Min(maximum, available));
        return Math.Clamp(Math.Ceiling(desired), Math.Min(minimum, limit), limit);
    }
}
