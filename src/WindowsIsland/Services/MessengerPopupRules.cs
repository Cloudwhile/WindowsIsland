namespace WindowsIsland.Services;

internal static class MessengerPopupRules
{
    public static bool IsMessagePopup(string className, long style, long extendedStyle, int width, int height, bool owned)
    {
        if (width is < 120 or > 640 || height is < 40 or > 380) return false;
        if ((style & 0x70000) != 0) return false; // A resizable chat window is not a message popup.
        if (className is "#32768" or "#32770" || className.Contains("tooltip", StringComparison.OrdinalIgnoreCase)
            || className.Contains("QTipLabel", StringComparison.OrdinalIgnoreCase)) return false;
        var namedPopup = className.Contains("notification", StringComparison.OrdinalIgnoreCase)
            || className.Contains("notify", StringComparison.OrdinalIgnoreCase)
            || className.Contains("toast", StringComparison.OrdinalIgnoreCase);
        var tool = (extendedStyle & 0x80) != 0;
        var topmost = (extendedStyle & 8) != 0;
        return namedPopup || topmost && (tool || owned);
    }
}
