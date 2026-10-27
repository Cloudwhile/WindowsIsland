using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace WindowsIsland.Services;

// 摘要由构建时的原生载荷生成并嵌入宿主，不信任 DLL 旁可替换的摘要文件。
internal sealed class TelegramHookIntegrity : IDisposable
{
    private readonly List<SafeFileHandle> _directories = [];
    private FileStream? _file;
    public SafeFileHandle Handle => _file!.SafeFileHandle;

    public static TelegramHookIntegrity Open(string path)
    {
        var verified = new TelegramHookIntegrity();
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
            using var resource = typeof(TelegramHookIntegrity).Assembly.GetManifestResourceStream("WindowsIsland.TelegramHook.sha256")
                ?? throw Invalid();
            if (resource.Length > 256) throw Invalid();
            using var reader = new StreamReader(resource);
            var expected = reader.ReadToEnd().Trim();
            if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)
                || !string.Equals(Convert.ToHexString(SHA256.HashData(verified._file)), expected, StringComparison.OrdinalIgnoreCase)) throw Invalid();
            return verified;
        }
        catch { verified.Dispose(); throw new TelegramHookIntegrityException(); }
    }

    public void Dispose()
    {
        _file?.Dispose();
        for (var index = _directories.Count - 1; index >= 0; index--) _directories[index].Dispose();
        _directories.Clear();
    }

    private static TelegramHookIntegrityException Invalid() => new();

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

internal sealed class TelegramHookIntegrityException : IOException
{
    public TelegramHookIntegrityException() : base("Telegram hook integrity validation failed.")
        => HResult = unchecked((int)0x8007000D);
}
