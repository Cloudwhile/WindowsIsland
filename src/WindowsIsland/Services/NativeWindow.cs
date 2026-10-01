using System.Runtime.InteropServices;

namespace WindowsIsland.Services;

internal static class NativeWindow
{
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);

    public static void RemoveSystemBorder(nint hwnd)
    {
        var style = GetWindowLongPtr(hwnd, -16).ToInt64();
        if ((style & 0x00CC0000L) != 0)
        {
            style = (style & ~0x00CC0000L) | 0x80000000L;
            SetWindowLongPtr(hwnd, -16, (nint)style);
            SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x37);
        }
        // The island has its own animated window region, independent of DWM corners.
        int noCorners = 1, noBorder = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(hwnd, 33, ref noCorners, sizeof(int));
        DwmSetWindowAttribute(hwnd, 34, ref noBorder, sizeof(int));
    }

    public static void Round(nint hwnd, int width, int height, int radius)
    {
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        // Windows owns the region after a successful call.
        if (region != 0 && SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region);
    }
}
