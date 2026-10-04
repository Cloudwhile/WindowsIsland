using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        var titleBar = new TitleBar
        {
            Title = "Windows Island",
            IconSource = new ImageIconSource { ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Icons/LOGO.png")) }
        };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(page, 1);
        layout.Children.Add(titleBar);
        layout.Children.Add(page);
        Content = layout;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(titleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Island.ico"));
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = NativeWindow.GetDpiForWindow(hwnd) / 96d;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(640 * scale), area.Width - 48);
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
