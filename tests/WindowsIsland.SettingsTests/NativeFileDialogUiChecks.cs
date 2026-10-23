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
        Invoke(import);
        var dialog = await WaitDialogAsync(owner);
        check(GetWindowThreadProcessId(dialog, out _) != GetWindowThreadProcessId(owner, out _),
            "The import file dialog runs on a separate native thread");
        var ticks = 0;
        for (var i = 0; i < 4; i++) { await Task.Delay(50); ticks++; }
        check(ticks == 4 && !import.IsEnabled && IsWindowVisible(dialog),
            "The WinUI dispatcher keeps processing continuations while the import dialog is open");
        check(watch.Elapsed < TimeSpan.FromSeconds(8), "The native import dialog opens within eight seconds: " + watch.ElapsedMilliseconds + " ms");
        PostMessage(dialog, 0x0010, 0, 0);
        await UntilAsync(() => import.IsEnabled);
        check(import.IsEnabled, "Cancelling the native picker restores the import action");

        var package = Path.Combine(AppContext.BaseDirectory, "invalid-dialog-fixture.zip");
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create)) zip.CreateEntry("no-manifest.txt");
        Invoke(import);
        dialog = await WaitDialogAsync(owner);
        await SelectPathAsync(dialog, package);
        await UntilAsync(() => import.IsEnabled);
        check(Tree(page).OfType<InfoBar>().Any(bar => bar.Visibility == Visibility.Visible && bar.Message == Localization.Get("PluginErrorManifest")),
            "The selected ZIP reaches plugin validation and the page remains usable after rejection");

        var target = Path.Combine(AppContext.BaseDirectory, "dialog-report.json");
        var save = DiagnosticSaveDialog.PickAsync(owner, Localization.Get("DiagnosticsFileType"), DateTime.Now);
        dialog = await WaitDialogAsync(owner);
        await SelectPathAsync(dialog, target);
        check(await save.WaitAsync(TimeSpan.FromSeconds(5)) == target && !File.Exists(target),
            "The diagnostic save dialog returns the selected path without creating or overwriting it");
        await File.WriteAllTextAsync(target, "preserved");
        save = DiagnosticSaveDialog.PickAsync(owner, Localization.Get("DiagnosticsFileType"), DateTime.Now);
        dialog = await WaitDialogAsync(owner);
        PostMessage(dialog, 0x0010, 0, 0);
        check(await save.WaitAsync(TimeSpan.FromSeconds(5)) is null && await File.ReadAllTextAsync(target) == "preserved",
            "Cancelling the shared native save dialog preserves an existing report");
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
                if (GetWindow(hwnd, 4) != owner || !IsWindowVisible(hwnd)) return true;
                GetWindowThreadProcessId(hwnd, out var process);
                var name = new System.Text.StringBuilder(64);
                GetClassName(hwnd, name, name.Capacity);
                if (process == Environment.ProcessId && name.ToString() == "#32770") { found = hwnd; return false; }
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
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint state);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, System.Text.StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
