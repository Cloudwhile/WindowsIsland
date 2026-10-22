using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WindowsIsland.Services;

internal static class ShellAppIcon
{
    public static byte[]? Read(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;
        IShellItemImageFactory? factory = null;
        nint bitmapHandle = 0;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName("shell:AppsFolder\\" + appId, 0, ref iid, out factory) < 0 || factory is null)
                return null;
            // Ask for the application icon rather than a document thumbnail.
            if (factory.GetImage(new Size(128, 128), 0x00000005, out bitmapHandle) < 0 || bitmapHandle == 0) return null;
            return EncodeBitmap(bitmapHandle);
        }
        catch (Exception) { return null; }
        finally
        {
            if (bitmapHandle != 0) DeleteObject(bitmapHandle);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }

    internal static byte[]? EncodeBitmap(nint handle)
    {
        if (GetObjectW(handle, Marshal.SizeOf<NativeBitmap>(), out var source) == 0
            || source.Width <= 0 || source.Height <= 0 || source.Width > 1024 || source.Height > 1024) return null;
        var info = new BitmapInfo { Size = 40, Width = source.Width, Height = -source.Height,
            Planes = 1, BitCount = 32 };
        var pixels = new byte[checked(source.Width * source.Height * 4)];
        var dc = GetDC(0);
        if (dc == 0) return null;
        try
        {
            if (GetDIBits(dc, handle, 0, (uint)source.Height, pixels, ref info, 0) != source.Height) return null;
        }
        finally { ReleaseDC(0, dc); }
        var hasAlpha = false;
        for (var index = 3; index < pixels.Length; index += 4) hasAlpha |= pixels[index] != 0;
        // Shell DIBs use premultiplied BGRA. PNG stores straight alpha.
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var alpha = hasAlpha ? pixels[index + 3] : (byte)255;
            pixels[index + 3] = alpha;
            for (var channel = 0; channel < 3; channel++)
                pixels[index + channel] = alpha == 0 ? (byte)0 : (byte)Math.Min(255, (pixels[index + channel] * 255 + alpha / 2) / alpha);
        }
        using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, source.Width, source.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var row = 0; row < source.Height; row++)
                Marshal.Copy(pixels, row * source.Width * 4, data.Scan0 + row * data.Stride, source.Width * 4);
        }
        finally { bitmap.UnlockBits(data); }
        using var memory = new MemoryStream();
        bitmap.Save(memory, ImageFormat.Png);
        return memory.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public nint Bits; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPels, YPels; public uint ColorsUsed, ColorsImportant;
    }
    [DllImport("gdi32.dll")] private static extern int GetObjectW(nint handle, int size, out NativeBitmap bitmap);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte[] bits, ref BitmapInfo info, uint usage);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string name, nint context, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint bitmap);

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(Size size, uint flags, out nint bitmap);
    }
}
