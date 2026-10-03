using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowsIsland.Services;

internal static class NativeWindow
{
    private sealed record ContentBounds(int X, int Y, int Width, int Height, int Radius);
    private static readonly Dictionary<nint, ContentBounds> HitAreas = [];
    private const uint FrameStyles = 0x00CF0000, FrameExtendedStyles = 0x00020301;
    private const nuint FrameSubclassId = 3;
    private static readonly SubclassProc FrameCallback = BorderlessWindowProc;

    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint color, byte alpha, uint flags);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);

    public static void ConfigureOverlay(nint hwnd)
    {
        const long noActivate = 0x08000000, toolWindow = 0x80, appWindow = 0x40000, transparent = 0x20, layered = 0x80000;
        var style = GetWindowLongPtr(hwnd, -20).ToInt64();
        SetWindowLongPtr(hwnd, -20, (nint)((style | noActivate | toolWindow | transparent | layered) & ~appWindow));
        SetLayeredWindowAttributes(hwnd, 0, 255, 2);
        SetWindowPos(hwnd, (nint)(-1), 0, 0, 0, 0, 0x33);
    }

    public static void EnsureTopmost(nint hwnd) => SetWindowPos(hwnd, (nint)(-1), 0, 0, 0, 0, 0x0213);

    public static void SetContentBounds(nint hwnd, int x, int y, int width, int height, int radius) =>
        HitAreas[hwnd] = new(x, y, width, height, radius);

    public static void RemoveSystemBorder(nint hwnd)
    {
        if (!SetWindowSubclass(hwnd, FrameCallback, FrameSubclassId, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var style = GetWindowLongPtr(hwnd, -16).ToInt64();
        var extended = GetWindowLongPtr(hwnd, -20).ToInt64();
        var frameChanged = (style & FrameStyles) != 0 || (extended & FrameExtendedStyles) != 0;
        SetWindowLongPtr(hwnd, -16, (nint)((style & ~((long)FrameStyles)) | 0x80000000L));
        SetWindowLongPtr(hwnd, -20, (nint)(extended & ~((long)FrameExtendedStyles)));
        if (frameChanged)
            SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x37);
        SetWindowRgn(hwnd, 0, false);
        ExtendGlass(hwnd);
        int noCorners = 1, noBorder = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(hwnd, 33, ref noCorners, sizeof(int));
        DwmSetWindowAttribute(hwnd, 34, ref noBorder, sizeof(int));
    }

    private static nint BorderlessWindowProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x031E) ExtendGlass(hwnd); // WM_DWMCOMPOSITIONCHANGED
        if (message is 0x0021 or 0x024B) return 3; // MA_NOACTIVATE / PA_NOACTIVATE
        if (message == 0x0084 && HitAreas.TryGetValue(hwnd, out var bounds)) // WM_NCHITTEST
        {
            var point = new Point { X = unchecked((short)lParam.ToInt64()), Y = unchecked((short)(lParam.ToInt64() >> 16)) };
            ScreenToClient(hwnd, ref point);
            var localX = point.X - bounds.X;
            var localY = point.Y - bounds.Y;
            if (localX < 0 || localY < 0 || localX >= bounds.Width || localY >= bounds.Height) return -1;
            var radius = Math.Min(bounds.Radius, Math.Min(bounds.Width, bounds.Height) / 2);
            var nearestX = Math.Clamp(localX, radius, bounds.Width - radius);
            var nearestY = Math.Clamp(localY, radius, bounds.Height - radius);
            if ((localX - nearestX) * (localX - nearestX) + (localY - nearestY) * (localY - nearestY) > radius * radius) return -1;
            return 1;
        }
        // AppWindow can restore frame styles during resize; keep the full window as the client area.
        if (message == 0x0083) return 0; // WM_NCCALCSIZE
        if (message == 0x0085) return 0; // WM_NCPAINT
        if (message == 0x0086) return 1; // WM_NCACTIVATE
        if (message == 0x0046 && lParam != 0) // WM_WINDOWPOSCHANGING
        {
            var result = DefSubclassProc(hwnd, message, wParam, lParam);
            var position = Marshal.PtrToStructure<WindowPosition>(lParam);
            position.Flags |= 0x0010; // SWP_NOACTIVATE
            if ((position.Flags & (0x0004 | 0x0080)) == 0) position.InsertAfter = (nint)(-1);
            Marshal.StructureToPtr(position, lParam, fDeleteOld: false);
            return result;
        }
        if (message == 0x007C && lParam != 0) // WM_STYLECHANGING
        {
            var result = DefSubclassProc(hwnd, message, wParam, lParam);
            var index = unchecked((int)wParam);
            var change = Marshal.PtrToStructure<WindowStyleChange>(lParam);
            if (index == -16) change.NewStyle = (change.NewStyle & ~FrameStyles) | 0x80000000;
            if (index == -20) change.NewStyle &= ~FrameExtendedStyles;
            Marshal.StructureToPtr(change, lParam, fDeleteOld: false);
            return result;
        }
        if (message == 0x0082) { HitAreas.Remove(hwnd); RemoveWindowSubclass(hwnd, FrameCallback, FrameSubclassId); } // WM_NCDESTROY
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private static void ExtendGlass(nint hwnd)
    {
        var margins = new Margins { Left = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint hwnd, ref Point point);
    [StructLayout(LayoutKind.Sequential)] private struct WindowStyleChange { public uint OldStyle, NewStyle; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPosition
    {
        public nint Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }
}
