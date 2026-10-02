using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WindowsIsland.Services;

internal static class TelegramAvatar
{
    public static byte[]? Encode(TelegramHookPacket packet, byte[] payload)
    {
        if (packet.AvatarLength == 0) return null;
        using var bitmap = new Bitmap(64, 64, PixelFormat.Format32bppPArgb);
        var locked = bitmap.LockBits(new Rectangle(0, 0, 64, 64), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try { Marshal.Copy(payload, packet.TextLength, locked.Scan0, packet.AvatarLength); }
        finally { bitmap.UnlockBits(locked); }
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
