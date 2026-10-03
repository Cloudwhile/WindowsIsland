using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

public sealed class IslandAnimationProbe : IDisposable
{
    private readonly IntPtr window;
    private readonly Thread thread;
    private readonly List<double> frames = new List<double>();
    private volatile bool stopped;
    private int moves;
    public int WindowChanges { get { return Volatile.Read(ref moves); } }

    public IslandAnimationProbe(IntPtr handle)
    {
        window = handle;
        thread = new Thread(Sample) { IsBackground = true };
        thread.Start();
    }

    private void Sample()
    {
        SetThreadDpiAwarenessContext(new IntPtr(-4));
        var clock = Stopwatch.StartNew();
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        IntPtr image = IntPtr.Zero, original = IntPtr.Zero, bits = IntPtr.Zero;
        var previous = new Rect();
        var width = 0; var height = 0; var hash = 0u; var initialized = false;
        try
        {
            while (!stopped)
            {
                Rect bounds;
                if (!IsWindowVisible(window) || !GetWindowRect(window, out bounds)) { Thread.Sleep(8); continue; }
                if (bounds.Left != previous.Left || bounds.Top != previous.Top || bounds.Right != previous.Right || bounds.Bottom != previous.Bottom)
                {
                    Interlocked.Increment(ref moves);
                    previous = bounds;
                }
                var nextWidth = bounds.Right - bounds.Left;
                var nextHeight = bounds.Bottom - bounds.Top;
                if (nextWidth <= 0 || nextHeight <= 0) { Thread.Sleep(8); continue; }
                if (nextWidth != width || nextHeight != height)
                {
                    if (image != IntPtr.Zero) { SelectObject(memory, original); DeleteObject(image); }
                    width = nextWidth; height = nextHeight;
                    var info = new BitmapInfo { Size = (uint)Marshal.SizeOf(typeof(BitmapInfo)), Width = width, Height = -height, Planes = 1, BitCount = 32 };
                    image = CreateDIBSection(screen, ref info, 0, out bits, IntPtr.Zero, 0);
                    if (image == IntPtr.Zero) return;
                    original = SelectObject(memory, image);
                }
                if (BitBlt(memory, 0, 0, width, height, screen, bounds.Left, bounds.Top, 0x40CC0020))
                {
                    GdiFlush();
                    var next = 2166136261u;
                    // Sample the notification interior; desktop margins and the clock are excluded.
                    for (var y = 18; y < Math.Min(height - 18, 230); y += 5)
                        for (var x = width / 3; x < width * 2 / 3; x += 5)
                            next = unchecked((next ^ (uint)Marshal.ReadInt32(bits, (y * width + x) * 4)) * 16777619u);
                    if (initialized && hash != next) { lock (frames) frames.Add(clock.Elapsed.TotalMilliseconds); }
                    hash = next; initialized = true;
                }
                Thread.Sleep(8);
            }
        }
        finally
        {
            if (image != IntPtr.Zero) { SelectObject(memory, original); DeleteObject(image); }
            DeleteDC(memory); ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public double[] Snapshot() { lock (frames) return frames.ToArray(); }
    public void Dispose() { stopped = true; thread.Join(1000); }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, ImageSize; public int XPixels, YPixels; public uint ColorsUsed, ColorsImportant;
    }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint operation);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
