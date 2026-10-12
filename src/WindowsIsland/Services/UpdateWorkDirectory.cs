using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WindowsIsland.Services;

internal static class UpdateWorkDirectory
{
    // Packaged desktop apps can write to a redirected LocalAppData directory.
    // The unpackaged helper needs the path of the file actually opened by Windows.
    public static string Resolve(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (!OperatingSystem.IsWindows()) return directory;
        var probe = Path.Combine(directory, ".path-" + Guid.NewGuid().ToString("N"));
        using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.DeleteOnClose);
        var path = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(file.SafeFileHandle, path, (uint)path.Capacity, 0);
        if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (length >= path.Capacity) throw new PathTooLongException();
        var resolved = path.ToString();
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) resolved = @"\\" + resolved[8..];
        else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
        return Path.GetDirectoryName(resolved)!;
    }

    internal static string Rebase(string path, string originalDirectory, string resolvedDirectory) =>
        Path.GetFullPath(Path.Combine(resolvedDirectory, Path.GetRelativePath(originalDirectory, path)));

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
}
