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
            if (factory.GetImage(new Size(48, 48), 0x00000005, out bitmapHandle) < 0 || bitmapHandle == 0) return null;
            using var bitmap = Image.FromHbitmap(bitmapHandle);
            using var memory = new MemoryStream();
            bitmap.Save(memory, ImageFormat.Png);
            return memory.ToArray();
        }
        catch (Exception) { return null; }
        finally
        {
            if (bitmapHandle != 0) DeleteObject(bitmapHandle);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }

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
