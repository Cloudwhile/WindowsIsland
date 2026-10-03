using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Automation;

namespace WindowsIsland.Services;

internal sealed class MessengerPopupReader
{
    private readonly Dictionary<string, byte[]> _icons = new(StringComparer.OrdinalIgnoreCase);

    public (string Title, string Body)? Read(nint window, string appName)
    {
        var root = AutomationElement.FromHandle(window);
        if (root is null) return null;
        var textCondition = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
        var elements = root.FindAll(TreeScope.Descendants, textCondition);
        var labels = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (AutomationElement element in elements)
        {
            if (labels.Count >= 32) break;
            try
            {
                if (element.Current.IsOffscreen || element.Current.IsPassword) continue;
                var text = element.Current.Name?.Trim() ?? "";
                if (text.Length == 0 && element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
                    text = ((TextPattern)pattern).DocumentRange.GetText(2048).Trim();
                if (text.Length > 2048) text = text[..2048];
                if (text.Length == 0 || IsAppLabel(text, appName) || !seen.Add(text)) continue;
                labels.Add(text);
            }
            catch (ElementNotAvailableException) { }
        }
        if (labels.Count == 0) return null;
        if (labels.Count == 1)
        {
            var title = root.Current.Name?.Trim() ?? "";
            return (title.Length == 0 || IsAppLabel(title, appName) ? appName : title, labels[0]);
        }
        return (labels[0], string.Join("\n", labels.Skip(1)));
    }

    public byte[]? ReadIcon(string executable)
    {
        if (_icons.TryGetValue(executable, out var cached)) return cached;
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(executable);
            if (icon is null) return null;
            using var bitmap = icon.ToBitmap();
            using var memory = new MemoryStream();
            bitmap.Save(memory, ImageFormat.Png);
            var bytes = memory.ToArray();
            if (_icons.Count >= 16) _icons.Clear();
            _icons[executable] = bytes;
            return bytes;
        }
        catch (Exception) { return null; }
    }

    private static bool IsAppLabel(string text, string appName) => text == appName
        || text is "微信" or "WeChat" or "Weixin" or "QQ" or "Telegram" or "Telegram Desktop";
}
