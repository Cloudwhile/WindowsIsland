using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WindowsIsland.Plugins;

internal static class PluginFileSafety
{
    public static void CheckSingleLink(string path)
    {
        using var stream = OpenSingleLinkRead(path);
    }

    internal static FileStream OpenSingleLinkRead(string path)
    {
        PluginPaths.CheckLinks(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (OperatingSystem.IsWindows()
                && (!GetFileInformationByHandle(stream.SafeFileHandle, out var info)
                    || info.NumberOfLinks != 1 || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0))
                throw new PluginException(PluginErrorCode.Path, "插件文件不能是硬链接或重解析点。");
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
