using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WindowsIsland.Services;

internal static class ApplicationArtwork
{
    public static Bitmap Render(string path, int size)
    {
        using var source = new Bitmap(path);
        var bounds = VisibleBounds(source);
        var side = Math.Max(bounds.Width, bounds.Height);
        var crop = new RectangleF(bounds.X + (bounds.Width - side) / 2f,
            bounds.Y + (bounds.Height - side) / 2f, side, side);
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, size, size), crop.X, crop.Y,
                crop.Width, crop.Height, GraphicsUnit.Pixel, attributes);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static Rectangle VisibleBounds(Bitmap bitmap)
    {
        var whole = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(whole, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var left = bitmap.Width; var top = bitmap.Height; var right = -1; var bottom = -1;
        try
        {
            var row = new byte[bitmap.Width * 4];
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (var x = 0; x < bitmap.Width; x++)
                {
                    if (row[x * 4 + 3] < 32) continue;
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                }
            }
        }
        finally { bitmap.UnlockBits(data); }
        return right < left ? whole : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }
}
