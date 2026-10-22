using System.Drawing;
using System.Runtime.InteropServices;
using WindowsIsland.Services;

var info = new BitmapInfo { Size = 40, Width = 2, Height = -2, Planes = 1, BitCount = 32 };
var handle = Native.CreateDIBSection(0, ref info, 0, out var bits, 0, 0);
if (handle == 0 || bits == 0) throw new Exception("Cannot create a Shell DIB fixture.");
try
{
    byte[] pixels = [0, 0, 0, 0, 0, 0, 128, 128, 20, 40, 60, 255, 0, 0, 255, 255];
    Marshal.Copy(pixels, 0, bits, pixels.Length);
    var png = ShellAppIcon.EncodeBitmap(handle) ?? throw new Exception("PNG encoding failed.");
    using var memory = new MemoryStream(png);
    using var bitmap = new Bitmap(memory);
    if (!Image.IsAlphaPixelFormat(bitmap.PixelFormat) || bitmap.GetPixel(0, 0).A != 0
        || bitmap.GetPixel(1, 0).A != 128 || bitmap.GetPixel(1, 0).R < 250 || bitmap.GetPixel(0, 1).R != 60)
        throw new Exception("Shell PNG lost alpha, colors or pixel orientation.");
    Console.WriteLine("PASS Shell PNG retains transparent corners, fractional alpha, straight colors and orientation");
}
finally { Native.DeleteObject(handle); }

[StructLayout(LayoutKind.Sequential)]
internal struct BitmapInfo
{
    public uint Size; public int Width, Height; public ushort Planes, BitCount;
    public uint Compression, ImageSize; public int X, Y; public uint Used, Important;
}
internal static class Native
{
    [DllImport("gdi32.dll")] internal static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint handle);
}
