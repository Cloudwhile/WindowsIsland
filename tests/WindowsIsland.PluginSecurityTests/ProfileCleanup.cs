using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WindowsIsland.PluginSecurityTests;

internal static class ProfileCleanup
{
    // 只清理此测试 GUID 根目录派生的固定 fixture profile，绝不枚举或删除正式产品 profile。
    public static async Task<bool> RemoveAsync(string root)
    {
        const string prefix = "windowsisland-plugin-security-";
        var name = Path.GetFileName(root);
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _)) return false;
        var success = true;
        foreach (var id in new[] { "wini_plugin_security_fixture", "wini_plugin_security_peer", "wini_plugin_security_lifecycle", "wini_plugin_security_stopping" })
        {
            var hostRoot = Path.Combine(root, "host-private");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(hostRoot).ToUpperInvariant() + "\n" + id)))[..40].ToLowerInvariant();
            var result = 0;
            for (var retry = 0; retry < 5; retry++)
            {
                result = DeleteAppContainerProfile("WindowsIsland.Plugin." + hash);
                if (result >= 0 || result is unchecked((int)0x80070002) or unchecked((int)0x80070490)) break;
                await Task.Delay(50);
            }
            success &= result >= 0 || result is unchecked((int)0x80070002) or unchecked((int)0x80070490);
        }
        return success;
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeleteAppContainerProfile(string name);
}
