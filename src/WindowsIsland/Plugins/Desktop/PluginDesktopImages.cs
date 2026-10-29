using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

internal sealed partial class PluginDesktopBroker
{
    public Task<byte[]?> ReadImageAsync(DesktopImage request, CancellationToken token = default) => RunAsync(() =>
    {
        if (!_trees.TryGetValue(request.Token, out var tree) || Stopwatch.GetElapsedTime(tree.Created) > TimeSpan.FromSeconds(5)
            || request.Node < 0 || request.Node >= tree.Elements.Length || !Current(tree, request.Node)) return null;
        var client = GetClient(tree.ProcessId);
        try
        {
            if (request.Bounds is not { } bounds)
            {
                var node = tree.Elements[request.Node];
                var values = new List<string> { node.Current.HelpText };
                if (node.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)) values.Add(((ValuePattern)pattern).Current.Value);
                foreach (var value in values)
                {
                    if (value.Length is 0 or > 2048 || value.StartsWith(@"\\", StringComparison.Ordinal)) continue;
                    Uri.TryCreate(value, UriKind.Absolute, out var uri);
                    if (uri?.IsUnc == true) continue;
                    var path = uri?.IsFile == true ? uri.LocalPath : Path.IsPathFullyQualified(value) ? value : null;
                    if (path is null || !File.Exists(path)) continue;
                    var clientRoot = Path.GetDirectoryName(client.Process.MainModule?.FileName ?? "")
                        ?? throw new UnauthorizedAccessException();
                    if (!Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(clientRoot) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)) continue;
                    PluginPaths.CheckLinks(path);
                    using var stream = PluginFileSafety.OpenSingleLinkRead(path);
                    if (stream.Length is <= 0 or > 4 * 1024 * 1024) continue;
                    using var image = Image.FromStream(stream, false, true);
                    if (image.Width is <= 0 or > 4096 || image.Height is <= 0 or > 4096) continue;
                    if (Current(tree, request.Node)) return Encode(image);
                }
                return null;
            }
            if (bounds.IsEmpty || bounds.Width is < 20 or > 256 || bounds.Height is < 20 or > 256
                || !tree.Value.Nodes[request.Node].Bounds.Contains(bounds) || tree.Value.Nodes[request.Node].Offscreen
                || IsIconic(tree.Window) || !IsWindowVisible(tree.Window) || IsHungAppWindow(tree.Window)) return null;
            var previousDpi = SetThreadDpiAwarenessContext((nint)(-4));
            try
            {
                if (tree.Frame is null && !Capture(tree)) return null;
                var rect = tree.FrameBounds;
                var windowBounds = new DesktopRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                if (!windowBounds.Contains(bounds)) return null;
                var frame = tree.Frame!;
                if (!tree.Captured.Contains(request.Node) || !Current(tree, request.Node)
                    || !GetWindowRect(tree.Window, out var after) || after != rect) return null;
                var crop = new Rectangle((int)Math.Round(bounds.X - rect.Left), (int)Math.Round(bounds.Y - rect.Top),
                    (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height));
                if (crop.X < 0 || crop.Y < 0 || crop.Right > frame.Width || crop.Bottom > frame.Height) return null;
                using var avatar = frame.Clone(crop, PixelFormat.Format24bppRgb);
                var colors = new HashSet<int>();
                for (var y = 4; y < avatar.Height - 4; y += Math.Max(1, avatar.Height / 8))
                    for (var x = 4; x < avatar.Width - 4; x += Math.Max(1, avatar.Width / 8)) colors.Add(avatar.GetPixel(x, y).ToArgb());
                return colors.Count >= 2 ? Encode(avatar) : null;
            }
            finally { if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or OutOfMemoryException or ExternalException or InvalidOperationException) { return null; }
    }, token);

    private static bool Capture(Tree tree)
    {
        if (!GetWindowRect(tree.Window, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top
            || (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top) > 16777216) return false;
        var candidates = Enumerable.Range(0, tree.Elements.Length).Where(index => !tree.Value.Nodes[index].Offscreen
            && !tree.Value.Nodes[index].Bounds.IsEmpty && Current(tree, index)).ToArray();
        var frame = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top, PixelFormat.Format24bppRgb);
        try
        {
            using (var graphics = Graphics.FromImage(frame))
            {
                var dc = graphics.GetHdc();
                try { if (!PrintWindow(tree.Window, dc, 2)) return false; }
                finally { graphics.ReleaseHdc(dc); }
            }
            if (!GetWindowRect(tree.Window, out var after) || after != rect) return false;
            tree.Captured = candidates.Where(index => Current(tree, index)).ToHashSet();
            tree.FrameBounds = rect; tree.Frame = frame; frame = null!;
            return true;
        }
        finally { frame?.Dispose(); }
    }

    private static byte[]? ReadIcon(string path)
    {
        try { using var icon = Icon.ExtractAssociatedIcon(path); using var bitmap = icon?.ToBitmap(); return bitmap is null ? null : Encode(bitmap); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or ExternalException) { return null; }
    }

    private static byte[] Encode(Image image)
    {
        using var bitmap = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.DrawImage(image, new Rectangle(0, 0, 64, 64), 0, 0, image.Width, image.Height,
                GraphicsUnit.Pixel, attributes);
        }
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray();
    }
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}
