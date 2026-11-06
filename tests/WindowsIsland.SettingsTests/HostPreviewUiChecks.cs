using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI.ViewManagement;
using WindowsIsland.Components;
using WindowsIsland.Services;

namespace WindowsIsland;

// 只接收夹具已启动的私有宿主；预览入口、设置事件与通知呈现均使用生产路径。
internal static class HostPreviewUiChecks
{
    public static async Task RunAsync(MainWindow host, SettingsStore hostStore, Action<bool, string> check)
    {
        var storePath = Path.GetFullPath(Get<string>(hostStore, "_path"));
        var privateRoot = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!Get<bool>(host, "_verification") || !ReferenceEquals(Get<SettingsStore>(host, "_settings"), hostStore)
            || !storePath.StartsWith(privateRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Host preview checks require the existing verification host and its private output-directory settings.");
        if (Get<SettingsWindow?>(host, "_settingsWindow") is not null)
            throw new InvalidOperationException("Host preview checks require the private host's settings window to be closed initially.");

        var original = hostStore.Current;
        var output = Path.Combine(AppContext.BaseDirectory, "host-preview-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var scheduler = Get<IslandScheduler>(host, "_scheduler");
        var surface = Get<IslandSurface>(host, "_root");
        var canvas = Get<Grid>(host, "_host");
        var motion = Get<IslandMotion>(host, "_motion");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(host);
        var results = new List<object>();
        SettingsWindow? settingsWindow = null;
        var completed = false;
        var restored = false;
        var cleanupCompleted = false;
        try
        {
            // 不覆盖夹具此前仍在显示的消息，也不改变固定内容或来源开关。
            await UntilAsync(() => scheduler.Message is null, "A previous fixture notification did not expire.", 7);
            var pinnedId = scheduler.Pinned?.Id;
            var pinnedPlugin = scheduler.Pinned?.PluginId;
            hostStore.Save(original with { MuteDuringGames = false, MuteDuringFullScreen = false });
            host.OpenSettings();
            settingsWindow = Get<SettingsWindow?>(host, "_settingsWindow")
                ?? throw new InvalidOperationException("The real host did not create its settings window.");
            var page = Tree((DependencyObject)settingsWindow.Content).OfType<SettingsPage>().Single();
            await UntilAsync(() => page.IsLoaded, "The host settings page did not load.");
            var navigation = Tree(page).OfType<NavigationView>().Single();
            navigation.SelectedItem = navigation.MenuItems.OfType<NavigationViewItem>().Single(item => item.Tag is "appearance");
            await UntilAsync(() => Find<Button>(page, "PreviewActualNotification") is not null,
                "The real host's appearance preview action did not load.");
            page.Refresh();
            page.UpdateLayout();

            var animations = Find<ToggleSwitch>(page, "AnimationsToggle")!;
            var transitions = Find<ToggleSwitch>(page, "OpacityTransitionsToggle")!;
            animations.IsOn = false;
            transitions.IsOn = false;
            check(!hostStore.Current.Animations && !hostStore.Current.OpacityTransitions,
                "The real host settings controls persist disabled motion and opacity transitions before geometry checks");

            var acrylic = Find<ToggleSwitch>(page, "AcrylicToggle")!;
            var transparency = Find<Slider>(page, "BackgroundTransparencySlider")!;
            var preview = Find<Button>(page, "PreviewActualNotification")!;
            check(preview.IsEnabled && ToolTipService.GetToolTip(preview) is not null
                && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(preview)),
                "The real host appearance page exposes an enabled, named preview icon with a tooltip");

            var index = 0;
            foreach (var position in Enum.GetValues<NotificationPosition>())
            {
                var useAcrylic = index % 2 != 0;
                var transparencyPercent = index % 2 == 0 ? 25 : 65;
                index++;
                acrylic.IsOn = useAcrylic;
                transparency.Value = transparencyPercent;
                var choice = Find<ToggleButton>(page, "NotificationPosition" + position)!;
                if (choice.IsChecked != true)
                    ((IToggleProvider)new ToggleButtonAutomationPeer(choice).GetPattern(PatternInterface.Toggle)).Toggle();
                var expectedOpacity = 1 - transparencyPercent / 100d;
                await UntilAsync(() => hostStore.Current.Position == position
                    && Get<NotificationPosition>(host, "_position") == position,
                    "The host did not receive the selected notification position.");
                check(hostStore.Current.Acrylic == useAcrylic
                    && Math.Abs(hostStore.Current.BackgroundOpacity - expectedOpacity) < 0.001
                    && new SettingsStore(storePath).Current == hostStore.Current,
                    "Appearance controls immediately persist material, transparency and position for the real host: " + position);

                preview.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                page.UpdateLayout();
                var previousMessage = scheduler.Message;
                ((IInvokeProvider)new ButtonAutomationPeer(preview).GetPattern(PatternInterface.Invoke)).Invoke();
                await UntilAsync(() => IsPreview(scheduler.Message) && !ReferenceEquals(scheduler.Message, previousMessage)
                    && scheduler.State == IslandState.Expanded && scheduler.ExpandedKind == IslandExpandedKind.Message
                    && host.AppWindow.IsVisible && IsWindowVisible(hwnd) && Get<bool>(host, "_surfaceVisible") && !motion.IsRunning,
                    "The host preview action did not display its actual notification island.");
                await Task.Delay(120);
                canvas.UpdateLayout();
                var panel = Get<NotificationPanel>(host, "_notificationPanel");
                var title = Find<TextBlock>(panel, "NotificationTitle")!;
                var body = Find<TextBlock>(panel, "NotificationBody")!;
                check(panel.Visibility == Visibility.Visible && panel.ActualWidth > 0 && panel.ActualHeight > 0
                    && title.Text == Localization.Get("PreviewTitle") && body.Text == Localization.Get("PreviewBody"),
                    "The actual island renders the localized preview title and body: " + position);
                check(pinnedId is null || scheduler.Pinned?.Id == pinnedId && scheduler.Pinned.PluginId == pinnedPlugin,
                    "Displaying a real preview preserves the existing fixed content: " + position);
                if (scheduler.Pinned is not null)
                    check(Find<TextBlock>(canvas, "PinnedMinimalIcon") is { Visibility: Visibility.Visible }
                        && Find<Grid>(canvas, "PinnedCompact") is { Visibility: Visibility.Collapsed },
                        "A preview beside fixed content uses the message expansion and fixed-content side marker: " + position);

                var bounds = SurfaceScreenBounds(surface, canvas, hwnd);
                var workArea = Get<RectInt32>(host, "_workArea");
                var scale = GetDpiForWindow(hwnd) / 96d;
                check(Math.Abs(Get<double>(host, "_scale") - scale) < 0.01
                    && Math.Abs(surface.XamlRoot.RasterizationScale - scale) < 0.01,
                    "The actual island agrees with native per-window DPI before screen-coordinate validation: " + position);
                CheckPosition(bounds, workArea, position, scale, check);
                check(GetWindowRect(hwnd, out var nativeBounds), "The visible host exposes native bounds: " + position);
                var material = CheckMaterial(surface, hostStore.Current, check, position);
                var screenshot = "island-" + position + ".png";
                CaptureIsland(hwnd, bounds, Path.Combine(output, screenshot));
                results.Add(new
                {
                    Position = position.ToString(), NativeDpi = GetDpiForWindow(hwnd), RasterizationScale = surface.XamlRoot.RasterizationScale,
                    WorkArea = new { workArea.X, workArea.Y, workArea.Width, workArea.Height },
                    SurfaceBounds = new { bounds.X, bounds.Y, bounds.Width, bounds.Height },
                    HostBounds = new { nativeBounds.Left, nativeBounds.Top, nativeBounds.Right, nativeBounds.Bottom },
                    State = scheduler.State.ToString(), ExpandedKind = scheduler.ExpandedKind.ToString(),
                    HasFixedContent = scheduler.Pinned is not null, ActualTitle = title.Text, ActualBody = body.Text,
                    Preferences = new { hostStore.Current.Acrylic, hostStore.Current.BackgroundOpacity, hostStore.Current.Animations,
                        hostStore.Current.OpacityTransitions }, Material = material, Screenshot = screenshot
                });
            }
            completed = true;
        }
        finally
        {
            try
            {
                if (IsPreview(scheduler.Message))
                    typeof(MainWindow).GetMethod("DismissNotification", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(host, [false]);
                if (settingsWindow is not null && ReferenceEquals(Get<SettingsWindow?>(host, "_settingsWindow"), settingsWindow))
                    settingsWindow.Close();
                hostStore.Save(original);
                await UntilAsync(() => Get<SettingsWindow?>(host, "_settingsWindow") is null,
                    "The private host settings window did not close.");
                restored = hostStore.Current == original && new SettingsStore(storePath).Current == original;
                check(restored && Get<NotificationPosition>(host, "_position") == original.Position,
                    "Host preview checks close only the host's private settings window and restore its original persisted preferences");
                check(scheduler.Message is null && (scheduler.Pinned is null || scheduler.State == IslandState.Compact),
                    "Preview cleanup preserves the fixed island after dismissing only the fixture preview message");
                cleanupCompleted = true;
            }
            finally
            {
                File.WriteAllText(Path.Combine(output, "host-preview-ui.json"), JsonSerializer.Serialize(new
                {
                    Status = completed && restored && cleanupCompleted ? "Passed" : "Incomplete", PrivateVerificationHost = true,
                    ActualHostSettingsWindow = true, PreferencesRestored = restored, GlobalSettingsChanged = false,
                    Scope = "Real host appearance preview, visible island text, screen coordinates and material preferences; fixed content remains enabled.",
                    Results = results
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
    }

    private static object CheckMaterial(IslandSurface surface, AppSettings settings, Action<bool, string> check,
        NotificationPosition position)
    {
        var theme = Get<ThemeSettings?>(surface, "_theme");
        var system = Get<UISettings>(surface, "_systemSettings");
        bool highContrast, effects;
        try { highContrast = theme?.HighContrast ?? true; effects = !highContrast && system.AdvancedEffectsEnabled; }
        catch (COMException) { highContrast = true; effects = false; }
        var expected = Get<Brush>(surface, highContrast ? "_highContrast" : settings.Acrylic && effects ? "_acrylic" : "_solid");
        var expectedOpacity = effects ? settings.BackgroundOpacity : 1;
        var applied = Get<AppSettings>(surface, "_settings");
        check(applied.Acrylic == settings.Acrylic && Math.Abs(applied.BackgroundOpacity - settings.BackgroundOpacity) < 0.001
            && ReferenceEquals(surface.Background, expected) && Math.Abs(surface.Background.Opacity - expectedOpacity) < 0.001,
            "The actual island applies material and opacity preferences with the current system-effects fallback: " + position);
        return new { HighContrast = highContrast, AdvancedEffects = effects, BrushType = surface.Background.GetType().Name,
            ActualOpacity = surface.Background.Opacity, ExpectedOpacity = expectedOpacity };
    }

    private static void CheckPosition(Rect bounds, RectInt32 work, NotificationPosition position, double scale,
        Action<bool, string> check)
    {
        var horizontal = position switch
        {
            NotificationPosition.TopLeft or NotificationPosition.LeftCenter or NotificationPosition.BottomLeft => 0d,
            NotificationPosition.TopRight or NotificationPosition.RightCenter or NotificationPosition.BottomRight => 1d,
            _ => 0.5
        };
        var vertical = position switch
        {
            NotificationPosition.TopLeft or NotificationPosition.TopCenter or NotificationPosition.TopRight => 0d,
            NotificationPosition.BottomLeft or NotificationPosition.BottomCenter or NotificationPosition.BottomRight => 1d,
            _ => 0.5
        };
        var inset = 12 * scale;
        var x = work.X + inset + Math.Max(0, work.Width - bounds.Width - inset * 2) * horizontal;
        var y = work.Y + inset + Math.Max(0, work.Height - bounds.Height - inset * 2) * vertical;
        var tolerance = 2 * scale;
        check(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= work.X - tolerance && bounds.Top >= work.Y - tolerance
            && bounds.Right <= work.X + work.Width + tolerance && bounds.Bottom <= work.Y + work.Height + tolerance
            && Math.Abs(bounds.X - x) <= tolerance && Math.Abs(bounds.Y - y) <= tolerance,
            "The actual island reaches the selected screen anchor inside the current work area: " + position);
    }

    private static Rect SurfaceScreenBounds(FrameworkElement surface, Grid canvas, nint hwnd)
    {
        var bounds = surface.TransformToVisual(canvas).TransformBounds(new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
        var origin = new NativePoint();
        if (!ClientToScreen(hwnd, ref origin)) throw new InvalidOperationException("The island client origin is unavailable.");
        var scale = GetDpiForWindow(hwnd) / 96d;
        return new Rect(origin.X + bounds.X * scale, origin.Y + bounds.Y * scale, bounds.Width * scale, bounds.Height * scale);
    }

    private static void CaptureIsland(nint hwnd, Rect bounds, string path)
    {
        if (!IsWindowVisible(hwnd)) throw new InvalidOperationException("The actual island must be visible before its screen capture.");
        var x = (int)Math.Floor(bounds.X);
        var y = (int)Math.Floor(bounds.Y);
        var width = (int)Math.Ceiling(bounds.Right) - x;
        var height = (int)Math.Ceiling(bounds.Bottom) - y;
        using var bitmap = new System.Drawing.Bitmap(width, height);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(x, y, 0, 0, bitmap.Size, System.Drawing.CopyPixelOperation.SourceCopy);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static bool IsPreview(IslandNotification? message) => message is { Id: 0, AppName: "Windows Island", AppId: null };
    private static T Get<T>(object instance, string name)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().Name, name);
        return (T)field.GetValue(instance)!;
    }
    private static T? Find<T>(DependencyObject root, string id) where T : FrameworkElement => Tree(root).OfType<T>()
        .SingleOrDefault(element => AutomationProperties.GetAutomationId(element) == id);
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static async Task UntilAsync(Func<bool> predicate, string failure, int timeoutSeconds = 5)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(timeoutSeconds)) throw new TimeoutException(failure);
            await Task.Delay(25);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out NativeRect bounds);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
}
