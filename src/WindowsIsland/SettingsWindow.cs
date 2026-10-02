using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WindowsIsland.Components;
using WindowsIsland.Services;

namespace WindowsIsland;

internal sealed class SettingsWindow : Window
{
    private readonly DispatcherQueueTimer _refresh;

    public SettingsWindow(SettingsStore settings, Func<SettingsSnapshot> snapshot, Func<Task> initialize,
        Func<Task> requestAccess, Action preview)
    {
        Title = "Windows Island · 设置";
        SystemBackdrop = new MicaBackdrop();
        var page = new SettingsPage(settings, snapshot, initialize, requestAccess, preview, Close);
        Content = page;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = NativeWindow.GetDpiForWindow(hwnd) / 96d;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(560 * scale), area.Width - 48);
        var height = Math.Min((int)(720 * scale), area.Height - 48);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2, width, height));
        _refresh = DispatcherQueue.CreateTimer();
        _refresh.Interval = TimeSpan.FromSeconds(1);
        _refresh.Tick += (_, _) => page.Refresh();
        _refresh.Start();
        Closed += (_, _) => { _refresh.Stop(); page.Dispose(); };
    }
}
