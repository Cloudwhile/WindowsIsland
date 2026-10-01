using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowsIsland.Services;

internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private readonly nint _window, _icon, _menuOwner;
    private readonly SubclassProc _callback;
    private readonly Action _exit, _requestAccess;
    private readonly uint _taskbarCreated;
    private NotifyIconData _data;
    private bool _added, _disposed;
    private NotificationAccess _access = NotificationAccess.Waiting;
    private string _status = "正在连接通知";

    public TrayIcon(nint window, Action exit, Action requestAccess)
    {
        _window = window; _exit = exit; _requestAccess = requestAccess;
        _icon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "Island.ico"), 1, 32, 32, 0x10);
        if (_icon == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        // A zero-size native popup owns the tray menu without ever showing the island.
        _menuOwner = CreateWindowEx(0x80, "STATIC", "WindowsIsland.TrayMenu", 0x80000000, 0, 0, 0, 0, 0, 0, 0, 0);
        _callback = WindowProc;
        if (!SetWindowSubclass(window, _callback, 1, 0))
        {
            if (_menuOwner != 0) DestroyWindow(_menuOwner);
            DestroyIcon(_icon);
            throw new Win32Exception();
        }
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window, Id = 1,
            Flags = 1 | 2 | 4 | 0x80, CallbackMessage = CallbackMessage, Icon = _icon,
            Tip = "Windows Island · 正在连接通知", Info = "", InfoTitle = "", Version = 4
        };
        EnsureAdded();
    }

    public void EnsureAdded()
    {
        if (_disposed || _added) return;
        _added = ShellNotifyIcon(0, ref _data);
        if (_added) ShellNotifyIcon(4, ref _data);
    }

    public void UpdateAccess(NotificationAccess access)
    {
        _access = access;
        _status = access switch
        {
            NotificationAccess.Allowed => "通知监听中",
            NotificationAccess.Denied => "允许通知访问",
            NotificationAccess.NeedsRegistration => "请从开始菜单启动 Windows Island",
            NotificationAccess.Unavailable => "重试通知连接",
            _ => "开启通知访问"
        };
        _data.Tip = "Windows Island · " + _status;
        if (_added) ShellNotifyIcon(1, ref _data);
    }

    private nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (!_disposed)
        {
            if (message == _taskbarCreated) { _added = false; EnsureAdded(); }
            if (message == CallbackMessage)
            {
                var action = (uint)(lParam.ToInt64() & 0xFFFF);
                if (action is 0x007B or 0x0205 or 0x0400 or 0x0401) ShowMenu();
                return 0;
            }
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "tray-debug.log"), $"show menu owner={_menuOwner}\n");
        var menu = CreatePopupMenu();
        if (menu == 0) return;
        uint selected;
        try
        {
            var canRequest = _access is NotificationAccess.Denied or NotificationAccess.Waiting or NotificationAccess.Unavailable;
            AppendMenu(menu, canRequest ? 0u : 1u, 1, _status);
            AppendMenu(menu, 0x800, 0, "");
            AppendMenu(menu, 0, 2, "退出");
            GetCursorPos(out var point);
            ShowWindow(_menuOwner, 4);
            SetForegroundWindow(_menuOwner);
            selected = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, _menuOwner, 0);
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "tray-debug.log"), $"selection={selected}\n");
        }
        finally { DestroyMenu(menu); ShowWindow(_menuOwner, 0); }
        // Queue callbacks so the native message hook can return before a window is destroyed.
        if (selected == 1) _requestAccess();
        if (selected == 2) _exit();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_added) ShellNotifyIcon(2, ref _data);
        RemoveWindowSubclass(_window, _callback, 1);
        if (_menuOwner != 0) DestroyWindow(_menuOwner);
        DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string label);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint owner, nint rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
}
