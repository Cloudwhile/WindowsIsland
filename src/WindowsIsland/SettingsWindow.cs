using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WindowsIsland.Components;
using WindowsIsland.Services;

namespace WindowsIsland;

internal sealed class SettingsWindow : Window
{
    private readonly DispatcherQueueTimer _refresh;

    public SettingsWindow(SettingsStore settings, UpdateCheckService updates, Func<SettingsSnapshot> snapshot, Func<Task> initialize,
        Func<Task> requestAccess, Action preview, Action exit, bool openUpdates = false, bool verification = false,
        Func<string, Task>? retrySource = null, Func<string, bool, Task>? setSourceEnabled = null,
        Func<string, Task>? removePlugin = null, Plugins.PluginLibrary? pluginLibrary = null, Func<Task>? rescanPlugins = null,
        Func<string, Task<Plugins.PluginSettingsModel?>>? pluginSettings = null)
    {
        Title = Localization.Get("WindowSettingsTitle");
        var mica = Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported();
        if (mica) SystemBackdrop = new MicaBackdrop();
        var page = new SettingsPage(settings, updates, snapshot, initialize, requestAccess, preview, Close, exit, openUpdates, verification,
            PickDiagnosticFileAsync, retrySource, setSourceEnabled, removePlugin, pluginLibrary, PickPluginFileAsync, rescanPlugins, pluginSettings);
        var titleBar = (TitleBar)XamlReader.Load("<TitleBar xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Foreground='{ThemeResource TextFillColorPrimaryBrush}' />");
        titleBar.Title = "Windows Island";
        titleBar.IconSource = new ImageIconSource { ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Icons/LOGO.png")) };
        titleBar.Loaded += (_, _) => RefreshCaptionTheme(titleBar);
        titleBar.ActualThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(() => RefreshCaptionTheme(titleBar));
        var layout = (Grid)XamlReader.Load("<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource ApplicationPageBackgroundThemeBrush}' />");
        layout.Language = Localization.Language;
        if (mica) layout.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        LocalizedUI.Bind(layout, () =>
        {
            Title = Localization.Get("WindowSettingsTitle");
            layout.Language = Localization.Language;
        });
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
        var width = Math.Min((int)(840 * scale), area.Width - 48);
        var height = Math.Min((int)(720 * scale), area.Height - 48);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2, width, height));
        _refresh = DispatcherQueue.CreateTimer();
        _refresh.Interval = TimeSpan.FromSeconds(1);
        _refresh.Tick += (_, _) => page.Refresh();
        _refresh.Start();
        Closed += (_, _) => { _refresh.Stop(); page.Dispose(); };
    }

    private void RefreshCaptionTheme(TitleBar titleBar)
    {
        var caption = AppWindow.TitleBar;
        if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            caption.ButtonForegroundColor = caption.ButtonInactiveForegroundColor = null;
            caption.ButtonHoverForegroundColor = caption.ButtonPressedForegroundColor = null;
            caption.ButtonBackgroundColor = caption.ButtonInactiveBackgroundColor = null;
            caption.ButtonHoverBackgroundColor = caption.ButtonPressedBackgroundColor = null;
            return;
        }
        if (titleBar.Foreground is not SolidColorBrush brush) return;
        var color = brush.Color;
        caption.ButtonForegroundColor = caption.ButtonHoverForegroundColor = caption.ButtonPressedForegroundColor = color;
        caption.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0x99, color.R, color.G, color.B);
        caption.ButtonBackgroundColor = caption.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        caption.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x0F, color.R, color.G, color.B);
        caption.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x18, color.R, color.G, color.B);
    }

    private async Task<string?> PickDiagnosticFileAsync()
    {
        var fileType = Localization.Get("DiagnosticsFileType");
        return await DiagnosticSaveDialog.PickAsync(WinRT.Interop.WindowNative.GetWindowHandle(this), fileType, DateTime.Now);
    }

    private Task<string?> PickPluginFileAsync() => NativeFileDialog.OpenPluginAsync(
        WinRT.Interop.WindowNative.GetWindowHandle(this), Localization.Get("PluginPackageFileType"));
}
