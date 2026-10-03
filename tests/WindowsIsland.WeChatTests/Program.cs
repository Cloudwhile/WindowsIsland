using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using WindowsIsland.Services;

if (args.Length >= 3 && args[0] == "--fixture") return WeChatFixture.Run(args[1], args[2]);
if (args.Length > 0 && args[0] == "--observe") return LiveListener.Run();
if (args.Length == 0) return ListenerChecks.Run();
if (args[0] != "--inspect") throw new ArgumentException("Unknown verification mode.");
Trace.Listeners.Add(new ConsoleTraceListener());
var sessions = new List<WeChatAccessibilitySession>();
var ids = new HashSet<int>();
try
{
    foreach (var process in Process.GetProcessesByName("Weixin"))
    {
        using (process)
        {
            var session = WeChatAccessibilitySession.TryOpen(process);
            if (session is null) continue;
            sessions.Add(session);
            ids.Add(process.Id);
        }
    }
    Console.WriteLine($"Accessible clients: {ids.Count}");
    EnumWindows((window, _) =>
    {
        GetWindowThreadProcessId(window, out var id);
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        if (!ids.Contains((int)id) || !name.ToString().Contains("QWindowIcon")) return true;
        var root = AutomationElement.FromHandle(window);
        using var conversationReader = new WeChatReader(root, window);
        var snapshot = conversationReader.Read();
        Console.WriteLine($"READER sessions={snapshot.HasSessions} previews={snapshot.Previews.Count} messages={snapshot.HasMessages} rows={snapshot.Messages.Count} conversationChars={snapshot.Conversation.Length}");
        foreach (var preview in snapshot.Previews)
            Console.WriteLine($"PREVIEW conversationChars={preview.Conversation.Length} bodyChars={preview.Body.Length} unread={preview.Unread} testMessage={preview.Body == "1"} avatarBytes={preview.Avatar?.Length ?? 0}");
        var pending = new Queue<(AutomationElement Element, int Depth)>();
        pending.Enqueue((root, 0));
        var count = 0;
        while (pending.TryDequeue(out var node) && count++ < 500)
        {
            var data = node.Element.Current;
            var semantic = data.Name is "微信" or "消息" or "会话" or "聊天" or "搜索" or "文件传输助手"
                ? data.Name : $"<text {data.Name.Length} chars>";
            var safeId = data.AutomationId.StartsWith("session_item_") ? "session_item_<redacted>" : data.AutomationId;
            var shape = string.Concat(data.Name.Select(character => char.IsLetterOrDigit(character) ? '字' : character));
            if (shape.Length > 140) shape = shape[..140];
            Console.WriteLine($"{node.Depth} {data.ControlType.ProgrammaticName} class={data.ClassName} id={safeId} name={semantic} shape={shape.Replace("\n", " | ")} rect={data.BoundingRectangle}");
            if (data.ControlType == ControlType.ListItem)
            {
                var unread = System.Text.RegularExpressions.Regex.Match(data.Name, @"(\d+)\s*条.*?消息").Success;
                var patterns = string.Join(",", node.Element.GetSupportedPatterns().Select(pattern => pattern.ProgrammaticName));
                Console.WriteLine($"ROW class={data.ClassName} parts={data.Name.Split('\n').Length} unreadMarker={unread} statusChars={data.ItemStatus.Length} typeChars={data.ItemType.Length} helpChars={data.HelpText.Length} patterns={patterns}");
                var lines = data.Name.Split('\n');
                if (data.ClassName == "mmui::ChatSessionCell" && lines.Length > 1)
                {
                    var metadata = lines[1].Length < 16 && (lines[1].StartsWith("已") || lines[1].StartsWith("未"))
                        ? lines[1] : "<redacted>";
                    Console.WriteLine($"SESSION secondPart={metadata}");
                }
                if (node.Element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
                {
                    var value = ((ValuePattern)valuePattern).Current.Value;
                    var masked = string.Concat(value.Select(character => char.IsLetterOrDigit(character) ? '字' : character));
                    Console.WriteLine($"VALUE chars={value.Length} shape={masked.Replace("\n", " | ")}");
                }
            }
            if (data.ControlType == ControlType.List)
            {
                Console.WriteLine($"LIST patterns={string.Join(',', node.Element.GetSupportedPatterns().Select(pattern => pattern.ProgrammaticName))}");
                if (node.Element.TryGetCurrentPattern(GridPattern.Pattern, out var gridObject))
                {
                    var grid = (GridPattern)gridObject;
                    Console.WriteLine($"GRID rows={grid.Current.RowCount} columns={grid.Current.ColumnCount}");
                    var row = grid.GetItem(grid.Current.RowCount - 1, 0);
                    Console.WriteLine($"GRID lastClass={row.Current.ClassName} nameChars={row.Current.Name.Length} idChars={row.Current.AutomationId.Length}");
                }
                if (node.Element.TryGetCurrentPattern(ScrollPattern.Pattern, out var scrollObject))
                {
                    var scroll = ((ScrollPattern)scrollObject).Current;
                    Console.WriteLine($"SCROLL vertical={scroll.VerticallyScrollable} percent={scroll.VerticalScrollPercent} view={scroll.VerticalViewSize}");
                }
            }
            if (node.Depth >= 25) continue;
            var child = TreeWalker.RawViewWalker.GetFirstChild(node.Element);
            while (child is not null && pending.Count < 500)
            {
                pending.Enqueue((child, node.Depth + 1));
                child = TreeWalker.RawViewWalker.GetNextSibling(child);
            }
        }
        return true;
    }, 0);
}
finally { foreach (var session in sessions) session.Dispose(); }
return 0;

[DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, nint data);
[DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint processId);
[DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] static extern int GetClassName(nint window, StringBuilder name, int size);
delegate bool EnumProc(nint window, nint data);
