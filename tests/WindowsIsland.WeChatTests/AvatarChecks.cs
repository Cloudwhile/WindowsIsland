using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using WindowsIsland.Services;

internal static class AvatarChecks
{
    public static void Run(nint window, Action<string> send, Action<bool, string> check)
    {
        send("restore");
        Wait(() => !IsIconic(window));
        var root = AutomationElement.FromHandle(window);
        var sessions = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "session_list"));
        var viewport = sessions.Current.BoundingRectangle;
        using var reader = new WeChatAvatarReader(root, window);
        var rows = ReadRows();
        reader.BeginRead(rows);
        var group = rows.Single(row => row.Conversation == "示例群");
        var groupBytes = reader.ReadConversation(group, viewport);
        if (groupBytes is null) Console.WriteLine($"AVATAR unavailable: window={window} row={group.Bounds} viewport={viewport} current={group.IsCurrent()}");
        check(IsColor(groupBytes, Color.ForestGreen), "The captured frame starts with the expected group portrait");
        var service = rows.Single(row => row.Conversation == "服务号");
        var serviceBytes = reader.ReadConversation(service, viewport);
        check(IsColor(serviceBytes, Color.DarkOrange), "Service session rows retain their two-color logo without an accessible image child");

        try
        {
            send("avatar-recycle");
            Wait(() => !group.IsCurrent());
            var recycled = ReadRows().Single(row => row.Conversation == "替换后的会话");
            check(reader.ReadConversation(recycled, viewport) is null, "A row recycled after frame capture cannot inherit the former conversation's pixels");
            reader.BeginRead(ReadRows());
            check(IsColor(reader.ReadConversation(recycled, viewport), Color.DarkOrange), "The next coherent frame supplies the recycled row's actual portrait");
            using var staleReader = new WeChatAvatarReader(root, window);
            staleReader.BeginRead(ReadRows());
            send("avatar-reorder");
            Wait(() => !recycled.IsCurrent());
            check(staleReader.ReadConversation(recycled, viewport) is null, "A preview captured before list reordering is never bound to a later row position");
            var moved = ReadRows().Single(row => row.Conversation == "替换后的会话");
            staleReader.BeginRead(ReadRows());
            check(IsColor(staleReader.ReadConversation(moved, viewport), Color.DarkOrange), "Reordered conversations recover their portrait on the next read");
        }
        finally { send("avatar-reset"); Wait(() => ReadRows().Any(row => row.Conversation == "示例群")); }

        var directory = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N") + ".avatar-cache");
        try
        {
            var key = ("session_item_服务号", "服务号");
            var cache = new WeChatAvatarCache(directory, "client/window/1");
            cache.Store(key, serviceBytes!);
            File.SetLastWriteTimeUtc(Directory.GetFiles(directory).Single(), DateTime.UtcNow.AddMinutes(-3));
            var reopened = new WeChatAvatarCache(directory, "client/window/1");
            check(IsColor(reopened.Get(key)?.Bytes, Color.DarkOrange), "A recreated listener restores a confirmed service logo from its own client cache");
            check(new WeChatAvatarCache(directory, "client/window/2").Get(key) is null,
                "Identically named conversations in another client instance cannot share portraits");
            check(reopened.Get((key.Item1, "不同会话")) is null, "A reused automation key cannot return another conversation's cached portrait");
            using var restoredReader = new WeChatAvatarReader(root, window, reopened);
            send("minimize");
            Wait(() => IsIconic(window));
            var minimizedRows = ReadRows();
            restoredReader.BeginRead(minimizedRows);
            check(IsColor(restoredReader.ReadConversation(minimizedRows.Single(row => row.Conversation == "服务号"), viewport), Color.DarkOrange),
                "A cold reader can supply a cached service logo while the client is minimized");
            File.WriteAllText(Directory.GetFiles(directory).Single(), "invalid image");
            check(new WeChatAvatarCache(directory, "client/window/1").Get(key) is null, "An invalid cached image is ignored without interrupting message reading");
        }
        finally
        {
            if (Directory.Exists(directory)) { foreach (var path in Directory.GetFiles(directory)) File.Delete(path); Directory.Delete(directory); }
        }

        WeChatAvatarRow[] ReadRows() => sessions.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Cast<AutomationElement>().Select(element =>
            {
                var info = element.Current;
                var preview = WeChatText.ParsePreview(info.AutomationId, info.Name, [])!;
                return new WeChatAvatarRow(element, preview.Key, preview.Conversation, info.AutomationId,
                    info.Name, info.ClassName, info.BoundingRectangle, info.IsOffscreen);
            }).ToArray();
    }

    private static void Wait(Func<bool> ready)
    {
        for (var index = 0; index < 100; index++) { if (ready()) { Thread.Sleep(100); return; } Thread.Sleep(20); }
        throw new InvalidOperationException("Avatar fixture did not reach the requested state.");
    }

    private static bool IsColor(byte[]? bytes, Color expected)
    {
        if (bytes is null) return false;
        using var stream = new MemoryStream(bytes);
        using var image = new Bitmap(stream);
        var actual = image.GetPixel(8, 8);
        if (Math.Abs(actual.R - expected.R) >= 6 || Math.Abs(actual.G - expected.G) >= 6 || Math.Abs(actual.B - expected.B) >= 6)
            Console.WriteLine($"AVATAR color: actual={actual} expected={expected}");
        return Math.Abs(actual.R - expected.R) < 6 && Math.Abs(actual.G - expected.G) < 6 && Math.Abs(actual.B - expected.B) < 6;
    }

    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
}
