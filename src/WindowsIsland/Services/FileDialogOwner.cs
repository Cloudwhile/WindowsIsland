using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowsIsland.Services;

// owner 与原生对话框在同一辅助进程、同一 STA，不能把 WinUI HWND 设成 owner。
internal sealed class FileDialogOwner : IDisposable
{
    public const string ParentProperty = "WindowsIsland.FileDialog.Parent";
    public nint Handle { get; }
    private readonly TimerProc _timer;
    private readonly Action _tick;
    private bool _disposed;

    public FileDialogOwner(FileDialogRequest request, Action tick)
    {
        _tick = tick;
        _timer = (_, _, _, _) =>
        {
            // 断开管道等错误不能穿过原生回调边界，宿主会按超时回收辅助进程。
            try { _tick(); } catch (Exception) { }
        };
        Handle = CreateWindowEx(0x80, "STATIC", "WindowsIsland.FileDialog", 0x80000000,
            request.X, request.Y, request.Width, request.Height, 0, 0, 0, 0);
        if (Handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!SetProp(Handle, ParentProperty, (nint)request.ParentWindow) || SetTimer(Handle, 1, 100, _timer) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            Dispose();
            throw new Win32Exception(error);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        KillTimer(Handle, 1);
        RemoveProp(Handle, ParentProperty);
        DestroyWindow(Handle);
        GC.KeepAlive(_timer);
    }

    private delegate void TimerProc(nint window, uint message, nuint timer, uint time);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(
        uint extended, string type, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint module, nint data);
    [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetProp(nint window, string name, nint value);
    [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode)] private static extern nint RemoveProp(nint window, string name);
    [DllImport("user32.dll", SetLastError = true)] private static extern nuint SetTimer(nint window, nuint id, uint interval, TimerProc callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint window, nuint id);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
}
