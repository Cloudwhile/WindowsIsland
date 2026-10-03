using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Rect = System.Windows.Rect;

namespace WindowsIsland.Services;

internal sealed class WeChatAvatarReader(AutomationElement root, nint nativeWindow = 0) : IDisposable
{
    private sealed record CachedAvatar(byte[] Bytes, DateTimeOffset ReadAt);
    private readonly Dictionary<string, CachedAvatar> _cache = [];
    private Bitmap? _frame;
    private Rectangle _windowBounds;
    private bool _captureAttempted;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(2);

    public void BeginRead() { EndRead(); _captureAttempted = false; }
    public void EndRead() { _frame?.Dispose(); _frame = null; }

    public byte[]? ReadConversation(AutomationElement row, string key, Rect viewport)
    {
        _cache.TryGetValue(key, out var cached);
        if (cached is not null && DateTimeOffset.UtcNow - cached.ReadAt < RefreshInterval) return cached.Bytes;
        try
        {
            var info = row.Current;
            var avatar = FindImage(row);
            var bytes = avatar is null ? null : ReadImageReference(avatar);
            if (bytes is null && !info.IsOffscreen)
            {
                var bounds = avatar?.Current.BoundingRectangle ?? ConversationBounds(info.ClassName, info.BoundingRectangle);
                if (!bounds.IsEmpty && !viewport.IsEmpty && viewport.Contains(bounds))
                {
                    CaptureWindow();
                    // A recycled or moved session row must not inherit another conversation's portrait.
                    var after = row.Current;
                    if (after.AutomationId == info.AutomationId && after.Name == info.Name && after.BoundingRectangle == info.BoundingRectangle)
                        bytes = Crop(bounds);
                }
            }
            if (bytes is null) return cached?.Bytes;
            if (_cache.Count >= 128 && !_cache.ContainsKey(key)) _cache.Remove(_cache.Keys.First());
            _cache[key] = new(bytes, DateTimeOffset.UtcNow);
            return bytes;
        }
        catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or COMException
            or IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception
            or System.Runtime.InteropServices.ExternalException)
        {
            System.Diagnostics.Trace.WriteLine($"WeChat avatar: {error.GetType().Name}");
            return cached?.Bytes;
        }
    }

    internal static Rect ConversationBounds(string className, Rect row)
    {
        if (className != "mmui::ChatSessionCell" || row.IsEmpty || row.Height is < 40 or > 240 || row.Width < row.Height * 2) return Rect.Empty;
        // WeChat 4 exposes the session as one accessible row; its portrait is 40 within the 68-unit row.
        var size = Math.Round(row.Height * 40 / 68);
        return new(row.X + Math.Round(row.Height * 12 / 68), row.Y + Math.Round((row.Height - size) / 2), size, size);
    }

    private static AutomationElement? FindImage(AutomationElement row)
    {
        var rowBounds = row.Current.BoundingRectangle;
        var condition = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Image),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        foreach (AutomationElement element in row.FindAll(TreeScope.Descendants, condition))
        {
            var info = element.Current;
            var bounds = info.BoundingRectangle;
            if (bounds.IsEmpty || bounds.Width is < 20 or > 256 || Math.Abs(bounds.Width - bounds.Height) > 4
                || bounds.Left < rowBounds.Left || bounds.Right > rowBounds.Left + rowBounds.Width * 0.4) continue;
            if (info.ControlType == ControlType.Image || info.AutomationId.Contains("avatar", StringComparison.OrdinalIgnoreCase)
                || info.Name is "头像" or "Avatar") return element;
        }
        return null;
    }

    private static byte[]? ReadImageReference(AutomationElement image)
    {
        var values = new List<string> { image.Current.HelpText };
        if (image.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)) values.Add(((ValuePattern)pattern).Current.Value);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 2048) continue;
            if (Uri.TryCreate(value, UriKind.Absolute, out var location) && location.IsUnc) continue;
            var path = location is { IsFile: true } ? location.LocalPath
                : Path.IsPathFullyQualified(value) ? value : null;
            if (path is null || path.StartsWith(@"\\", StringComparison.Ordinal) || !File.Exists(path)) continue;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is <= 0 or > 4194304) continue;
            using var source = Image.FromStream(stream, false, true);
            if (source.Width is <= 0 or > 4096 || source.Height is <= 0 or > 4096) continue;
            return Encode(source);
        }
        return null;
    }

    private void CaptureWindow()
    {
        if (_captureAttempted) return;
        _captureAttempted = true;
        var window = nativeWindow != 0 ? nativeWindow : (nint)root.Current.NativeWindowHandle;
        if (window == 0 || IsIconic(window) || !IsWindowVisible(window) || IsHungAppWindow(window)) return;
        var previousDpi = SetThreadDpiAwarenessContext((nint)(-4));
        try
        {
            if (!GetWindowRect(window, out var rect)) return;
            var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            if (bounds.Width <= 0 || bounds.Height <= 0 || (long)bounds.Width * bounds.Height > 16777216) return;
            var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            try
            {
                using var graphics = Graphics.FromImage(bitmap);
                var dc = graphics.GetHdc();
                bool printed;
                try { printed = PrintWindow(window, dc, 2); }
                finally { graphics.ReleaseHdc(dc); }
                if (!printed || !GetWindowRect(window, out var after) || after != rect) { bitmap.Dispose(); return; }
                _frame = bitmap; _windowBounds = bounds;
            }
            catch { bitmap.Dispose(); throw; }
        }
        finally { if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi); }
    }

    private byte[]? Crop(Rect bounds)
    {
        if (_frame is null) return null;
        var crop = new Rectangle((int)Math.Round(bounds.X - _windowBounds.X), (int)Math.Round(bounds.Y - _windowBounds.Y),
            (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height));
        if (crop.Width is < 20 or > 256 || crop.Height is < 20 or > 256 || crop.X < 0 || crop.Y < 0
            || crop.Right > _frame.Width || crop.Bottom > _frame.Height) return null;
        using var avatar = _frame.Clone(crop, PixelFormat.Format24bppRgb);
        var colors = new HashSet<int>();
        for (var y = 4; y < avatar.Height - 4; y += Math.Max(1, avatar.Height / 8))
            for (var x = 4; x < avatar.Width - 4; x += Math.Max(1, avatar.Width / 8)) colors.Add(avatar.GetPixel(x, y).ToArgb());
        return colors.Count >= 3 ? Encode(avatar) : null;
    }

    private static byte[] Encode(Image image)
    {
        using var bitmap = new Bitmap(64, 64, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(image, 0, 0, bitmap.Width, bitmap.Height);
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    public void Dispose() { EndRead(); _cache.Clear(); }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(nint window);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}
