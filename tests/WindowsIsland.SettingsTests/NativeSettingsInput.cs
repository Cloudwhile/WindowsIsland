using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using UiAutomation = System.Windows.Automation;

namespace WindowsIsland;

/// <summary>仅向本次隔离设置窗口发送经过目标校验的真实键鼠输入。</summary>
internal static class NativeSettingsInput
{
    internal sealed class UnavailableException(string message) : InvalidOperationException(message);
    private const uint NoMove = 0x0002, NoSize = 0x0001, NoActivate = 0x0010, Show = 0x0040;
    private static readonly nint Topmost = new(-1), NotTopmost = new(-2);

    public static async Task EnsureForegroundAsync(Window window)
    {
        var hwnd = Handle(window, requireVisible: false);
        ShowWindow(hwnd, 9);
        window.Activate();
        SetForegroundWindow(hwnd);
        await Task.Delay(100);
        if (GetForegroundWindow() == hwnd) return;
        var raised = false;
        GetCursorPos(out var originalCursor);
        try
        {
            if (!SetWindowPos(hwnd, Topmost, 0, 0, 0, 0, NoMove | NoSize | NoActivate | Show))
                throw new UnavailableException("The isolated settings window could not be raised for safe activation.");
            raised = true;
            await Task.Delay(80);
            var point = CaptionPoint(window);
            RequireTarget(hwnd, point);
            MovePointer(point);
            await Task.Delay(35);
            RequireTarget(hwnd, point);
            RequireCursor(point);
            Send([
                new() { Type = 0, Data = new() { Mouse = new() { Flags = 0x0002 } } },
                new() { Type = 0, Data = new() { Mouse = new() { Flags = 0x0004 } } }
            ]);
            await WaitAsync(() => GetForegroundWindow() == hwnd, 1500,
                "A verified click on the isolated title bar did not activate the settings window.", unavailable: true);
        }
        finally
        {
            if (raised && !SetWindowPos(hwnd, NotTopmost, 0, 0, 0, 0, NoMove | NoSize | NoActivate))
                throw new InvalidOperationException("The isolated settings window could not leave temporary topmost mode.");
            SetCursorPos(originalCursor.X, originalCursor.Y);
        }
        if (GetForegroundWindow() != hwnd)
            throw new UnavailableException("The isolated settings window lost foreground activation before keyboard input.");
    }

    public static void PressKey(Window window, VirtualKey key)
    {
        var hwnd = Handle(window);
        if (GetForegroundWindow() != hwnd)
            throw new UnavailableException("Keyboard input requires the isolated settings window to be in the foreground.");
        var extended = key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down ? 1u : 0u;
        Send([
            new() { Type = 1, Data = new() { Keyboard = new() { VirtualKey = (ushort)key, Flags = extended } } },
            new() { Type = 1, Data = new() { Keyboard = new() { VirtualKey = (ushort)key, Flags = extended | 2 } } }
        ]);
    }

    public static async Task HoverAsync(Window window, FrameworkElement element)
    {
        var hwnd = Handle(window);
        var root = (FrameworkElement)window.Content;
        if (!element.IsLoaded || element.XamlRoot != root.XamlRoot || !HasAncestor(element, root))
            throw new InvalidOperationException("Hover input requires a loaded element belonging to the isolated settings window.");
        var value = ToolTipService.GetToolTip(element);
        var expected = value is ToolTip toolTip ? toolTip.Content?.ToString() : value?.ToString();
        if (string.IsNullOrWhiteSpace(expected))
            throw new InvalidOperationException("The target element has no tooltip configuration to verify.");
        await EnsureForegroundAsync(window);
        element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        await Task.Delay(120);
        root.UpdateLayout();
        var bounds = element.TransformToVisual(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException("The hover target has no visible bounds.");
        var local = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        if (!VisualTreeHelper.FindElementsInHostCoordinates(local, root)
                .Any(candidate => ReferenceEquals(candidate, element) || HasAncestor(candidate, element)))
            throw new InvalidOperationException("The hover target is covered by another element in the isolated window.");
        var target = ToScreen(hwnd, local);
        RequireTarget(hwnd, target);
        GetCursorPos(out var originalCursor);
        var entered = false;
        void OnEntered(object sender, PointerRoutedEventArgs args)
        {
            entered = true;
            var point = args.GetCurrentPoint(element);
            Record($"PointerEntered: {element.GetType().Name}; device={args.Pointer.PointerDeviceType}; "
                + $"contact={point.IsInContact}; leftPressed={point.Properties.IsLeftButtonPressed}.");
        }
        element.PointerEntered += OnEntered;
        try
        {
            var away = CaptionPoint(window);
            if (Math.Abs(away.X - target.X) < 12 && Math.Abs(away.Y - target.Y) < 12)
                throw new InvalidOperationException("The safe pointer leave point overlaps the hover target.");
            RequireTarget(hwnd, away);
            MovePointer(away);
            await Task.Delay(100);
            RequireTarget(hwnd, target);
            MovePointer(target);
            await WaitAsync(() => entered, 1500, "Real pointer movement did not enter the isolated tooltip target.", unavailable: true);
            try
            {
                await WaitAsync(() =>
                {
                    if (GetForegroundWindow() != hwnd)
                        throw new UnavailableException("The settings window lost foreground focus during real pointer hover.");
                    RequireCursor(target);
                    return NativePopupHasText(element, expected);
                }, 2500,
                    "Real pointer hover did not display the configured text in a native tooltip popup.");
            }
            catch (InvalidOperationException error) when (error is not UnavailableException)
            {
                // Some native tooltip windows use a separate XamlRoot. Query only this process's visible tooltips.
                var nativeQuery = Task.Run(VisibleNativeTooltipNames);
                if (await Task.WhenAny(nativeQuery, Task.Delay(1200)) != nativeQuery)
                    throw new UnavailableException("The native tooltip automation query did not complete within its verification bound.");
                var names = await nativeQuery;
                if (!names.Contains(expected, StringComparer.Ordinal))
                    throw new InvalidOperationException("Real pointer entry was observed, but the native tooltip text was not visible. "
                        + $"Expected: {expected}; UIA tooltips: {string.Join(" | ", names)}; "
                        + $"XamlRoot popups: {VisualTreeHelper.GetOpenPopupsForXamlRoot(element.XamlRoot).Count}.");
            }
            var attached = ToolTipService.GetToolTip(element) as ToolTip;
            Record($"Hover verified: {element.GetType().Name}; expected={expected}; "
                + $"attachedNativeOpen={attached?.IsOpen}; nativeSize={attached?.ActualWidth}x{attached?.ActualHeight}; "
                + $"popups={VisualTreeHelper.GetOpenPopupsForXamlRoot(element.XamlRoot).Count}.");
            if (GetForegroundWindow() != hwnd)
                throw new UnavailableException("The settings window lost foreground focus during tooltip verification.");
        }
        finally
        {
            element.PointerEntered -= OnEntered;
            SetCursorPos(originalCursor.X, originalCursor.Y);
        }
    }

    private static bool NativePopupHasText(FrameworkElement owner, string expected) =>
        ToolTipService.GetToolTip(owner) is ToolTip { IsOpen: true, IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 } native
            && native.Content?.ToString() == expected
        || VisualTreeHelper.GetOpenPopupsForXamlRoot(owner.XamlRoot).Any(popup => popup.IsOpen && popup.Child is not null
            && Tree(popup.Child).Any(element => element is TextBlock text && text.Text == expected
                || element is ToolTip tip && tip.IsOpen && tip.Content?.ToString() == expected));

    private static void Record(string text) => File.AppendAllText(Path.Combine(AppContext.BaseDirectory,
        "native-settings-input.log"), DateTime.UtcNow.ToString("O") + " " + text + Environment.NewLine);

    private static string[] VisibleNativeTooltipNames()
    {
        var names = new List<string>();
        var process = new UiAutomation.PropertyCondition(UiAutomation.AutomationElement.ProcessIdProperty, Environment.ProcessId);
        var condition = new UiAutomation.PropertyCondition(UiAutomation.AutomationElement.ControlTypeProperty, UiAutomation.ControlType.ToolTip);
        foreach (UiAutomation.AutomationElement window in UiAutomation.AutomationElement.RootElement.FindAll(UiAutomation.TreeScope.Children, process))
        {
            try
            {
                if (window.Current.ControlType == UiAutomation.ControlType.ToolTip && !window.Current.IsOffscreen)
                    names.Add(window.Current.Name);
                foreach (UiAutomation.AutomationElement tip in window.FindAll(UiAutomation.TreeScope.Descendants, condition))
                    if (!tip.Current.IsOffscreen) names.Add(tip.Current.Name);
            }
            catch (UiAutomation.ElementNotAvailableException) { }
        }
        return names.ToArray();
    }

    private static NativePoint CaptionPoint(Window window)
    {
        var hwnd = Handle(window);
        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();
        var title = Tree(root).OfType<TitleBar>().Single();
        var bounds = title.TransformToVisual(root).TransformBounds(new Rect(0, 0, title.ActualWidth, title.ActualHeight));
        if (bounds.Width < 240 || bounds.Height < 16)
            throw new InvalidOperationException("The isolated title bar has no safe activation region.");
        foreach (var fraction in new[] { 0.5, 0.6, 0.4 })
        {
            var point = ToScreen(hwnd, new Point(bounds.X + bounds.Width * fraction, bounds.Y + bounds.Height / 2));
            RequireTarget(hwnd, point);
            var parameter = (nint)((point.Y & 0xffff) << 16 | point.X & 0xffff);
            var result = SendMessageTimeout(hwnd, 0x0084, 0, parameter, 0x0002, 200, out var hit);
            if (result != 0 && hit == 2) return point;
        }
        throw new UnavailableException("No verified draggable title-bar point is available for safe activation.");
    }

    private static NativePoint ToScreen(nint hwnd, Point point)
    {
        var scale = GetDpiForWindow(hwnd) / 96d;
        var result = new NativePoint { X = (int)Math.Round(point.X * scale), Y = (int)Math.Round(point.Y * scale) };
        if (!ClientToScreen(hwnd, ref result)) throw new InvalidOperationException("The settings coordinate conversion failed.");
        return result;
    }

    private static nint Handle(Window window, bool requireVisible = true)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        GetWindowThreadProcessId(hwnd, out var process);
        if (process != Environment.ProcessId || requireVisible && !IsWindowVisible(hwnd) || !IsWindowEnabled(hwnd))
            throw new InvalidOperationException("Native input is restricted to this process's visible, enabled verification window.");
        return hwnd;
    }

    private static void RequireTarget(nint hwnd, NativePoint point)
    {
        var hit = WindowFromPoint(point);
        if (hit == 0 || GetAncestor(hit, 2) != hwnd)
            throw new UnavailableException("The verified input point is covered by a window outside the isolated settings window.");
    }

    private static void RequireCursor(NativePoint expected)
    {
        if (!GetCursorPos(out var actual) || Math.Abs(actual.X - expected.X) > 2 || Math.Abs(actual.Y - expected.Y) > 2)
            throw new UnavailableException("The pointer did not reach the verified settings-window point.");
    }

    private static void MovePointer(NativePoint point)
    {
        var left = GetSystemMetrics(76);
        var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78);
        var height = GetSystemMetrics(79);
        Send([new() { Type = 0, Data = new() { Mouse = new()
        {
            X = (int)((point.X - left) * 65535d / Math.Max(1, width - 1)),
            Y = (int)((point.Y - top) * 65535d / Math.Max(1, height - 1)), Flags = 0xC001
        } } }]);
    }

    private static void Send(NativeInput[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) != inputs.Length)
            throw new UnavailableException("Native input to the isolated settings window was rejected.");
    }

    private static async Task WaitAsync(Func<bool> condition, int milliseconds, string failure, bool unavailable = false)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.ElapsedMilliseconds > milliseconds)
            {
                if (unavailable) throw new UnavailableException(failure);
                throw new InvalidOperationException(failure);
            }
            await Task.Delay(25);
        }
    }
    private static bool HasAncestor(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var nested in Tree(VisualTreeHelper.GetChild(root, index))) yield return nested;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public nuint ExtraInfo; }
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeout(nint hwnd, uint message, nint wParam,
        nint lParam, uint flags, uint timeout, out nint result);
}
