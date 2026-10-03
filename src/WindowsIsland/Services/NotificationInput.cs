using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace WindowsIsland.Services;

internal sealed class NotificationInput : IDisposable
{
    private readonly nint _window;
    private readonly SubclassProc _callback;
    private readonly Action<bool> _clicked;
    private RectInt32? _bounds;
    private int _radius;
    private bool _disposed;

    public NotificationInput(nint owner, Action<bool> clicked)
    {
        _clicked = clicked;
        _callback = WindowProc;
        _window = CreateWindowEx(0x08080080, "STATIC", "WindowsIsland.NotificationInput", 0x80000004,
            0, 0, 0, 0, owner, 0, 0, 0);
        if (_window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!SetWindowSubclass(_window, _callback, 1, 0))
        {
            var error = Marshal.GetLastWin32Error();
            DestroyWindow(_window);
            throw new Win32Exception(error);
        }
        // A nearly transparent input window leaves the compositor surface independent of native clipping.
        SetLayeredWindowAttributes(_window, 0, 1, 2);
        int noCorners = 1, noBorder = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(_window, 33, ref noCorners, sizeof(int));
        DwmSetWindowAttribute(_window, 34, ref noBorder, sizeof(int));
    }

    public void Show(RectInt32 bounds, int radius)
    {
        if (_disposed) return;
        if (_bounds != bounds)
            SetWindowPos(_window, (nint)(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010);
        if (_bounds?.Width != bounds.Width || _bounds?.Height != bounds.Height || _radius != radius)
        {
            var region = CreateRoundRectRgn(0, 0, bounds.Width + 1, bounds.Height + 1, radius * 2, radius * 2);
            if (region == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (SetWindowRgn(_window, region, false) == 0) { DeleteObject(region); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        }
        _bounds = bounds; _radius = radius;
        ShowWindow(_window, 8);
    }

    public void Hide() { if (!_disposed) ShowWindow(_window, 0); }

    private nint WindowProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x0084) return 1;
        if (message is 0x0021 or 0x024B) return 3;
        if (message is 0x0202 or 0x0205 && !_disposed) { _clicked(message == 0x0205); return 0; }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveWindowSubclass(_window, _callback, 1);
        DestroyWindow(_window);
    }

    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extended, string type, string title, uint style, int x, int y, int width, int height, nint owner, nint menu, nint module, nint data);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint window, uint color, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
