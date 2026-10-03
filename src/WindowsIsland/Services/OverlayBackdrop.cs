using Windows.UI.Composition;
using ICompositionSupportsSystemBackdrop = Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using System.Runtime.InteropServices;

namespace WindowsIsland.Services;

internal sealed class OverlayBackdrop : SystemBackdrop
{
    private Compositor? _compositor;
    private CompositionColorBrush? _brush;
    private Windows.System.DispatcherQueueController? _queue;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is null)
        {
            var options = new QueueOptions { Size = Marshal.SizeOf<QueueOptions>(), ThreadType = 2, ApartmentType = 2 };
            Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out var controller));
            try { _queue = WinRT.MarshalInterface<Windows.System.DispatcherQueueController>.FromAbi(controller); }
            finally { Marshal.Release(controller); }
        }
        _compositor = new Compositor();
        _brush = _compositor.CreateColorBrush(Color.FromArgb(0, 0, 0, 0));
        target.SystemBackdrop = _brush;
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        target.SystemBackdrop = null;
        _brush?.Dispose();
        _brush = null;
        _compositor?.Dispose();
        _compositor = null;
        base.OnTargetDisconnected(target);
        if (_queue is not null) { _ = _queue.ShutdownQueueAsync(); _queue = null; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct QueueOptions { public int Size, ThreadType, ApartmentType; }
    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(QueueOptions options, out nint controller);
}
