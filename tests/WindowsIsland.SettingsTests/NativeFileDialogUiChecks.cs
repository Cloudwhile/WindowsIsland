using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Microsoft.UI.Xaml;
using AutomationProperties = Microsoft.UI.Xaml.Automation.AutomationProperties;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WindowsIsland.Services;

namespace WindowsIsland;

// 仅由隔离的设置验证程序编译，不读取正式插件或用户文档。
internal static class NativeFileDialogUiChecks
{
    public static async Task RunAsync(Window window, FrameworkElement page, Action<bool, string> check)
    {
        var owner = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var import = Tree(page).OfType<Button>().Single(e => AutomationProperties.GetAutomationId(e) == "InstallPlugin");
        var watch = Stopwatch.StartNew();
        ArmWatchdog();
        await ClickAsync(window, import);
        var dialog = await WaitDialogAsync(owner);
        check(GetForegroundWindow() == dialog, "A real mouse click opens an interactive foreground file dialog");
        GetWindowThreadProcessId(dialog, out var workerId);
        var proxy = GetWindow(dialog, 4);
        check(workerId != Environment.ProcessId && GetWindowThreadProcessId(dialog, out _) == GetWindowThreadProcessId(proxy, out _)
            && GetWindow(proxy, 4) == 0, "The file dialog and its independent owner share a helper thread without owning the WinUI window");
        var ticks = 0;
        for (var i = 0; i < 4; i++) { await Task.Delay(50); ticks++; }
        check(ticks == 4 && !import.IsEnabled && IsWindowVisible(dialog),
            "The WinUI dispatcher keeps processing continuations while the import dialog is open");
        check(watch.Elapsed < TimeSpan.FromSeconds(8), "The native import dialog opens within eight seconds: " + watch.ElapsedMilliseconds + " ms");
        PostMessage(dialog, 0x0010, 0, 0);
        await UntilAsync(() => import.IsEnabled);
        DisarmWatchdog();
        check(import.IsEnabled, "Cancelling the native picker restores the import action");

        ArmWatchdog();
        Invoke(import);
        dialog = await WaitDialogAsync(owner);
        GetWindowThreadProcessId(dialog, out workerId);
        using (var worker = Process.GetProcessById((int)workerId))
        {
            var cancel = Tree(page).OfType<Button>().Single(e => AutomationProperties.GetAutomationId(e) == "CancelPluginInstallation");
            check(IsWindowEnabled(owner) && cancel.IsEnabled, "The settings window can cancel a separate file dialog");
            Invoke(cancel);
            await UntilAsync(() => import.IsEnabled && worker.HasExited);
            check(worker.HasExited, "Cancelling import terminates only its helper and restores the action");
        }
        DisarmWatchdog();

        ArmWatchdog();
        Invoke(import);
        dialog = await WaitDialogAsync(owner);
        GetWindowThreadProcessId(dialog, out workerId);
        using (var worker = Process.GetProcessById((int)workerId))
        {
            worker.Kill();
            await UntilAsync(() => import.IsEnabled);
            check(Tree(page).OfType<InfoBar>().Any(bar => bar.Visibility == Visibility.Visible && bar.Severity == InfoBarSeverity.Error),
                "A crashed helper reports a failure and allows another import");
        }
        DisarmWatchdog();

        await CheckUnresponsiveHelperAsync(owner, import, check);

        var package = Path.Combine(AppContext.BaseDirectory, "invalid-dialog-fixture.zip");
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create)) zip.CreateEntry("no-manifest.txt");
        ArmWatchdog();
        Invoke(import);
        dialog = await WaitDialogAsync(owner);
        await SelectPathAsync(dialog, package);
        await UntilAsync(() => import.IsEnabled);
        DisarmWatchdog();
        check(Tree(page).OfType<InfoBar>().Any(bar => bar.Visibility == Visibility.Visible && bar.Message == Localization.Get("PluginErrorManifest")),
            "The selected ZIP reaches plugin validation and the page remains usable after rejection");

        var target = Path.Combine(AppContext.BaseDirectory, "dialog-report.json");
        ArmWatchdog();
        var save = DiagnosticSaveDialog.PickAsync(owner, Localization.Get("DiagnosticsFileType"), DateTime.Now);
        dialog = await WaitDialogAsync(owner);
        await SelectPathAsync(dialog, target);
        check(await save.WaitAsync(TimeSpan.FromSeconds(5)) == target && !File.Exists(target),
            "The diagnostic save dialog returns the selected path without creating or overwriting it");
        DisarmWatchdog();
        await File.WriteAllTextAsync(target, "preserved");
        ArmWatchdog();
        save = DiagnosticSaveDialog.PickAsync(owner, Localization.Get("DiagnosticsFileType"), DateTime.Now);
        dialog = await WaitDialogAsync(owner);
        PostMessage(dialog, 0x0010, 0, 0);
        check(await save.WaitAsync(TimeSpan.FromSeconds(5)) is null && await File.ReadAllTextAsync(target) == "preserved",
            "Cancelling the shared native save dialog preserves an existing report");
        DisarmWatchdog();
    }

    private static void ArmWatchdog() => File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "file-dialog-watchdog"), "active");
    private static void DisarmWatchdog() => File.Delete(Path.Combine(AppContext.BaseDirectory, "file-dialog-watchdog"));

    private static async Task CheckUnresponsiveHelperAsync(nint owner, Button import, Action<bool, string> check)
    {
        ArmWatchdog();
        Invoke(import);
        var dialog = await WaitDialogAsync(owner);
        var threadId = GetWindowThreadProcessId(dialog, out var processId);
        using var worker = Process.GetProcessById((int)processId);
        // 只挂起本次私有辅助进程的对话框线程，验证系统消息超时保护。
        var thread = OpenThread(0x2, false, threadId);
        if (thread == 0) throw new InvalidOperationException("The isolated dialog thread could not be opened.");
        try
        {
            await Task.Delay(500);
            if (SuspendThread(thread) == uint.MaxValue) throw new InvalidOperationException("The isolated dialog thread could not be suspended.");
            var watch = Stopwatch.StartNew();
            var probes = 0;
            var shell = FindWindow("Shell_TrayWnd", null);
            while (!import.IsEnabled && watch.Elapsed < TimeSpan.FromSeconds(9))
            {
                var responsive = await Task.Run(() => SendMessageTimeout(owner, 0, 0, 0, 0x23, 150, out _) != 0);
                check(responsive, "The WinUI input queue responds while the private dialog thread is stalled");
                if (shell != 0)
                    check(await Task.Run(() => SendMessageTimeout(shell, 0x000E, 0, 0, 0x23, 150, out _) != 0),
                        "Explorer still answers bounded window queries while the private dialog thread is stalled");
                probes++;
                await Task.Delay(350);
            }
            check(probes > 0 && worker.HasExited && import.IsEnabled,
                "An unresponsive dialog helper is reclaimed automatically without blocking the host");
        }
        finally
        {
            if (!worker.HasExited) { ResumeThread(thread); worker.Kill(); }
            CloseHandle(thread);
            DisarmWatchdog();
        }
    }

    public static async Task CheckWindowCloseAsync(Window window, FrameworkElement page, Action<bool, string> check)
    {
        ArmWatchdog();
        var owner = WinRT.Interop.WindowNative.GetWindowHandle(window);
        Invoke(Tree(page).OfType<Button>().Single(e => AutomationProperties.GetAutomationId(e) == "InstallPlugin"));
        var dialog = await WaitDialogAsync(owner);
        GetWindowThreadProcessId(dialog, out var processId);
        using var worker = Process.GetProcessById((int)processId);
        window.Close();
        await UntilAsync(() => worker.HasExited);
        check(worker.HasExited, "Closing settings cancels and reclaims the active file dialog helper");
        DisarmWatchdog();
    }

    private static async Task ClickAsync(Window window, Button button)
    {
        var owner = WinRT.Interop.WindowNative.GetWindowHandle(window);
        window.Activate();
        ShowWindow(owner, 9);
        SetForegroundWindow(owner);
        var root = (FrameworkElement)window.Content;
        var position = button.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(button.ActualWidth / 2, button.ActualHeight / 2));
        var point = new NativePoint { X = (int)Math.Round(position.X * root.XamlRoot.RasterizationScale),
            Y = (int)Math.Round(position.Y * root.XamlRoot.RasterizationScale) };
        if (!ClientToScreen(owner, ref point) || !GetCursorPos(out var previous) || !SetCursorPos(point.X, point.Y))
            throw new InvalidOperationException("The native click could not be positioned.");
        try
        {
            // 桌面前台锁可能拒绝 SetForegroundWindow；由真实点击激活可见的测试窗口。
            SetWindowPos(owner, -1, 0, 0, 0, 0, 0x13);
            if (GetAncestor(WindowFromPoint(point), 2) != owner)
                throw new InvalidOperationException("The import button is covered by another window.");
            var inputs = new[] { new Input { Type = 0, Mouse = new MouseInput { Flags = 0x0002 } },
                new Input { Type = 0, Mouse = new MouseInput { Flags = 0x0004 } } };
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
                throw new InvalidOperationException("The native import click failed.");
            await Task.Delay(100);
        }
        finally { SetWindowPos(owner, -2, 0, 0, 0, 0, 0x13); SetCursorPos(previous.X, previous.Y); }
    }

    private static async Task SelectPathAsync(nint dialog, string path) => await Task.Run(() =>
    {
        var root = AutomationElement.FromHandle(dialog);
        AutomationElement? name = null;
        var watch = Stopwatch.StartNew();
        while (name is null && watch.Elapsed < TimeSpan.FromSeconds(8))
        {
            var field = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "1148"));
            if (field is not null)
                name = field.TryGetCurrentPattern(ValuePattern.Pattern, out _) ? field : field.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            if (name is null)
            {
                var host = root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "FileNameControlHost"));
                name = host?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            }
            if (name is null) Thread.Sleep(40);
        }
        if (name is null)
        {
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "native-dialog-structure.log"),
                root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                    .Select(element => element.Current.ControlType.ProgrammaticName + "/" + element.Current.AutomationId));
            throw new InvalidOperationException("The native dialog filename field was not ready.");
        }
        ((ValuePattern)name.GetCurrentPattern(ValuePattern.Pattern)).SetValue(path);
        // 原生打开按钮可以是 SplitButton；不能只匹配 Button 控件类型。
        var accept = root.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.AutomationIdProperty, "1"), new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton))))
            ?? throw new InvalidOperationException("The native dialog confirmation action was not found.");
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "native-dialog-controls.log"),
            "Filename=" + ((ValuePattern)name.GetCurrentPattern(ValuePattern.Pattern)).Current.Value + "\n"
            + "Confirm=" + accept.Current.ControlType.ProgrammaticName + "/" + accept.Current.AutomationId + "\n");
        ((InvokePattern)accept.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    });

    private static async Task<nint> WaitDialogAsync(nint owner)
    {
        nint found = 0;
        await UntilAsync(() =>
        {
            EnumWindows((hwnd, _) =>
            {
                var proxy = GetWindow(hwnd, 4);
                if (proxy == 0 || GetProp(proxy, FileDialogOwner.ParentProperty) != owner || !IsWindowVisible(hwnd)) return true;
                GetWindowThreadProcessId(hwnd, out var process);
                var name = new System.Text.StringBuilder(64);
                GetClassName(hwnd, name, name.Capacity);
                if (process != Environment.ProcessId && name.ToString() == "#32770") { found = hwnd; return false; }
                return true;
            }, 0);
            return found != 0;
        });
        return found;
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Native file dialog verification timed out.");
            await Task.Delay(40);
        }
    }

    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Tree(VisualTreeHelper.GetChild(root, i))) yield return item;
    }

    private delegate bool WindowCallback(nint hwnd, nint state);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public MouseInput Mouse;
    }
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint state);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, System.Text.StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string type, string? title);
    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint hwnd, string name);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] private static extern nint SendMessageTimeout(nint hwnd,
        uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("kernel32.dll")] private static extern nint OpenThread(uint access, bool inherit, uint thread);
    [DllImport("kernel32.dll")] private static extern uint SuspendThread(nint thread);
    [DllImport("kernel32.dll")] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
