using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace WindowsIsland.Services;

// 仅面板打开时观察点外与 Esc，不申请焦点、不记录键盘或鼠标内容。
internal sealed class PanelDismissMonitor(Action dismiss, Action<int>? scroll = null) : IDisposable
{
    private HookProc? _mouseCallback, _keyCallback;
    private nint _mouseHook, _keyHook;
    private RectInt32 _bounds;
    public void Show(RectInt32 bounds)
    {
        _bounds = bounds;
        if (_mouseHook != 0) return;
        _mouseCallback = Mouse;
        _keyCallback = Key;
        _mouseHook = SetWindowsHookEx(14, _mouseCallback, GetModuleHandle(null), 0);
        _keyHook = SetWindowsHookEx(13, _keyCallback, GetModuleHandle(null), 0);
        if (_mouseHook == 0 || _keyHook == 0) { var error = Marshal.GetLastWin32Error(); Hide(); throw new Win32Exception(error); }
    }
    private nint Mouse(int code, nuint message, nint data)
    {
        if (code >= 0 && message == 0x020A)
        {
            var point = Marshal.PtrToStructure<Point>(data);
            if (scroll is not null && point.X >= _bounds.X && point.Y >= _bounds.Y
                && point.X < _bounds.X + _bounds.Width && point.Y < _bounds.Y + _bounds.Height)
            {
                scroll(unchecked((short)(Marshal.ReadInt32(data, 8) >> 16)));
                return 1;
            }
        }
        if (code >= 0 && message is 0x0201 or 0x0204 or 0x0207)
        {
            var point = Marshal.PtrToStructure<Point>(data);
            if (point.X < _bounds.X || point.Y < _bounds.Y || point.X >= _bounds.X + _bounds.Width || point.Y >= _bounds.Y + _bounds.Height) dismiss();
        }
        return CallNextHookEx(0, code, message, data);
    }
    private nint Key(int code, nuint message, nint data)
    {
        if (code >= 0 && message is 0x0100 or 0x0104 && Marshal.ReadInt32(data) == 0x1B) dismiss();
        return CallNextHookEx(0, code, message, data);
    }
    public void Hide()
    {
        if (_mouseHook != 0) UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != 0) UnhookWindowsHookEx(_keyHook);
        _mouseHook = _keyHook = 0;
    }
    public void Dispose() => Hide();
    private delegate nint HookProc(int code, nuint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern nint SetWindowsHookEx(int kind, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
}
