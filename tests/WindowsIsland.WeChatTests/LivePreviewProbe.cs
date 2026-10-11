using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using WindowsIsland.Services;

internal static class LivePreviewProbe
{
    // Read only parsing outcomes. Never print names, identifiers, messages or avatars.
    public static int Run()
    {
        var ids = Process.GetProcessesByName("Weixin").Select(process => { using (process) return process.Id; }).ToHashSet();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var id);
            if (!ids.Contains((int)id)) return true;
            var name = new StringBuilder(256);
            GetClassName(window, name, name.Capacity);
            if (!name.ToString().Contains("QWindowIcon", StringComparison.Ordinal)) return true;
            var root = AutomationElement.FromHandle(window);
            var lists = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List));
            foreach (AutomationElement list in lists)
            {
                if (!WeChatText.IdMatches(list.Current.AutomationId, "session_list")) continue;
                var rows = list.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                var total = 0; var unread = 0; var mismatch = 0; var muted = 0; var missingStamp = 0;
                foreach (AutomationElement row in rows)
                {
                    var info = row.Current;
                    if (info.ClassName.Contains("Fold", StringComparison.Ordinal)) continue;
                    var parsed = WeChatText.ParsePreview(info.AutomationId, info.Name, []);
                    if (parsed is null) continue;
                    total++;
                    if (parsed.Unread > 0) unread++;
                    if (parsed.Muted) muted++;
                    if (parsed.Stamp.Length == 0) missingStamp++;
                    var first = info.Name.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (parsed.Conversation != first) mismatch++;
                }
                Console.WriteLine($"PREVIEWS rows={total} unreadRows={unread} mutedRows={muted} identityMismatch={mismatch} missingStamp={missingStamp}");
            }
            return true;
        }, 0);
        return 0;
    }

    private delegate bool EnumProc(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint id);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int count);
}
