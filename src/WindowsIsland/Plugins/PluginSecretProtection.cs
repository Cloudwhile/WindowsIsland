using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WindowsIsland.Plugins;

internal static class PluginSecretProtection
{
    private const string Prefix = "dpapi:v1:";
    public static bool IsProtected(string value) => value.StartsWith(Prefix, StringComparison.Ordinal);
    internal sealed record Migration(string Ciphertext, bool RequiresReset);
    internal static Migration MigrateLegacy(string id, string key, string value)
    {
        if (IsProtected(value))
        {
            if (Unprotect(id, key, value) is not null) return new(value, false);
            // 旧格式没有类型元数据。仍像旧密文的值隔离保存，不能把损坏密文当密码使用。
            try
            {
                if (Convert.FromBase64String(value[Prefix.Length..]).Length > 0)
                    return new(Protect(id, key, value), true);
            }
            catch (FormatException) { }
        }
        return new(Protect(id, key, value), false);
    }
    public static string Protect(string id, string key, string value) => Prefix + Convert.ToBase64String(Transform(id, key, Encoding.UTF8.GetBytes(value), true));
    public static string? Unprotect(string id, string key, string? value)
    {
        if (value is null || !value.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        byte[]? clear = null;
        try { clear = Transform(id, key, Convert.FromBase64String(value[Prefix.Length..]), false); return Encoding.UTF8.GetString(clear); }
        catch (Exception error) when (error is CryptographicException or FormatException) { return null; }
        finally { if (clear is not null) CryptographicOperations.ZeroMemory(clear); }
    }

    private static byte[] Transform(string id, string key, byte[] bytes, bool protect)
    {
        var entropy = SHA256.HashData(Encoding.UTF8.GetBytes(id + "\0" + key));
        var dataPin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        var entropyPin = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        var output = new Blob();
        try
        {
            var input = new Blob { Size = bytes.Length, Data = dataPin.AddrOfPinnedObject() };
            var extra = new Blob { Size = entropy.Length, Data = entropyPin.AddrOfPinnedObject() };
            var success = protect ? CryptProtectData(ref input, 0, ref extra, 0, 0, 1, out output)
                : CryptUnprotectData(ref input, 0, ref extra, 0, 0, 1, out output);
            if (!success) throw new CryptographicException("Secret storage failed.");
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (output.Data != 0)
            {
                for (var i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
            dataPin.Free(); entropyPin.Free();
            CryptographicOperations.ZeroMemory(bytes); CryptographicOperations.ZeroMemory(entropy);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public nint Data; }
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, nint description, ref Blob entropy, nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, nint description, ref Blob entropy, nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
