using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WindowsIsland.Plugin.Cli;

internal static class TestProfile
{
    public static async Task Remove(string testRoot, string id)
    {
        // 只接受本工具生成的 GUID 根，绝不枚举正式应用或其他测试的容器。
        var root = Path.GetFullPath(testRoot);
        if (!Guid.TryParseExact(Path.GetFileName(root), "N", out _)
            || Path.GetFileName(Path.GetDirectoryName(root)) != "tests"
            || Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(root))) != ".wini")
            throw new InvalidOperationException("Invalid SDK test root.");
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant() + "\n" + id)))[..40].ToLowerInvariant();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var result = DeleteAppContainerProfile("WindowsIsland.Plugin." + identity);
            if (result >= 0 || result is unchecked((int)0x80070002) or unchecked((int)0x80070490)) return;
            if (attempt == 4) throw new IOException("无法清理本次 SDK 测试容器。", result);
            await Task.Delay(100);
        }
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeleteAppContainerProfile(string name);
}
