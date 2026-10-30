using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace WindowsIsland.PluginIsolation;

// 摘要由已验证的插件提供；同句柄核验并持有文件与全部祖先目录。
public sealed class NativeBridgeIntegrity : IDisposable
{
    private readonly List<SafeFileHandle> _directories = [];
    private FileStream? _file;
    public SafeFileHandle Handle => _file!.SafeFileHandle;

    public static NativeBridgeIntegrity Open(string path, string expected)
    {
        var verified = new NativeBridgeIntegrity();
        try
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.AsSpan(2).Contains(':')) throw Invalid();
            var directories = new Stack<string>();
            for (var parent = Directory.GetParent(full); parent is not null; parent = parent.Parent) directories.Push(parent.FullName);
            // 从根到叶持有不共享删除的目录句柄，防止校验与 LoadLibrary 之间重命名目录。
            foreach (var directory in directories)
            {
                var handle = CreateFile(directory, 0x80, 3, 0, 3, 0x02200000, 0);
                if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(); }
                verified._directories.Add(handle);
                if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                    throw Invalid();
            }
            var file = CreateFile(full, 0x80000000, 1, 0, 3, 0x00200000, 0);
            if (file.IsInvalid) { file.Dispose(); throw new Win32Exception(); }
            verified._file = new FileStream(file, FileAccess.Read);
            if (!GetFileInformationByHandle(file, out var data) || data.NumberOfLinks != 1
                || (data.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0
                || verified._file.Length is <= 0 or > 64 * 1024 * 1024) throw Invalid();
            if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)
                || !string.Equals(Convert.ToHexString(SHA256.HashData(verified._file)), expected, StringComparison.OrdinalIgnoreCase)) throw Invalid();
            return verified;
        }
        catch { verified.Dispose(); throw new NativeBridgeIntegrityException(); }
    }

    public void Dispose()
    {
        _file?.Dispose();
        for (var index = _directories.Count - 1; index >= 0; index--) _directories[index].Dispose();
        _directories.Clear();
    }

    private static NativeBridgeIntegrityException Invalid() => new();

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
}

public sealed class NativeBridgeIntegrityException : IOException
{
    public NativeBridgeIntegrityException() : base("Telegram hook integrity validation failed.")
        => HResult = unchecked((int)0x8007000D);
}
