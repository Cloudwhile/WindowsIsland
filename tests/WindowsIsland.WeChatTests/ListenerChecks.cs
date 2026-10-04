using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using WindowsIsland.Services;

internal static class ListenerChecks
{
    public static int Run()
    {
        Trace.Listeners.Add(new ConsoleTraceListener());
        var checks = 0;
        var notifications = new ConcurrentQueue<IslandNotification>();
        var enabled = 1;
        using var listener = new WeChatService(() => Volatile.Read(ref enabled) == 1, action => action(), AppContext.BaseDirectory);
        listener.Received += notifications.Enqueue;
        var executable = Path.Combine(AppContext.BaseDirectory, "WeChat.exe");
        File.Copy(Environment.ProcessPath!, executable, true);
        using var fixture = FixtureClient.Start(executable);
        listener.Start();
        Check(Wait(() => listener.IsConnected(fixture.Process.Id)), "UIAutomation attaches to the separate fixture process");
        Quiet("Existing unread sessions and message history are not replayed");
        fixture.Send("incoming:第一条实时消息");
        Check(Wait(() => notifications.Count == 1), "Session and message events deliver one incoming notification");
        Check(notifications.TryDequeue(out var first) && first.Title == "示例会话" && first.Body == "第一条实时消息"
            && first.Source == NotificationSource.ClientAutomation && first.AppIcon is { Length: > 0 }, "Accessible content retains application identity and icon");
        Check(IsPortrait(first?.SenderAvatar, System.Drawing.Color.CornflowerBlue), "An accessible image reference supplies the personal conversation portrait");
        Quiet("Repeated reads do not duplicate the notification");
        fixture.Send("incoming:第一条实时消息");
        Check(Wait(() => notifications.Count == 1), "A second message with identical text still arrives");
        notifications.Clear();
        fixture.Send("outgoing:这是发出的消息");
        Quiet("Outgoing rows are filtered");
        fixture.Send("group:小林: 群聊的新消息");
        Check(Wait(() => notifications.Count == 1), "A conversation that is not open is read from its unread preview");
        Check(notifications.TryDequeue(out var group) && group.Title == "示例群" && group.Body == "小林: 群聊的新消息", "Group previews retain their conversation and sender");
        Check(IsPortrait(group?.SenderAvatar, System.Drawing.Color.ForestGreen), "A session with no accessible image exposes its group portrait through the client window");
        Check(!first!.SenderAvatar!.SequenceEqual(group!.SenderAvatar!), "Personal and group messages use their own conversation portraits");
        fixture.Send("mute:免打扰会话的新消息");
        Quiet("Muted conversation messages are not dispatched");
        Check(!listener.AllowsConversation("示例群", fixture.Process.Id), "Muted conversations are also blocked for other notification sources");
        fixture.Send("unmute:免打扰会话的新消息");
        Quiet("Disabling mute does not replay accumulated messages");
        Check(Wait(() => listener.AllowsConversation("示例群", fixture.Process.Id)), "The conversation becomes eligible after mute is disabled");
        fixture.Send("group:小林: 取消免打扰后的消息");
        Check(Wait(() => notifications.Count == 1), "New messages after disabling mute are delivered");
        notifications.Clear();
        fixture.Send("draft:尚未发送");
        Quiet("Draft changes remain silent");
        fixture.Send("history");
        Quiet("Replacing the visible history does not replay older rows");
        fixture.Process.Refresh();
        AvatarChecks.Run(fixture.Process.MainWindowHandle, fixture.Send, Check);
        fixture.Send("minimize");
        fixture.Send("incoming:最小化后收到的消息");
        Check(Wait(() => notifications.Count == 1), "UIAutomation continues reading a minimized client");
        Check(notifications.TryDequeue(out var minimized) && ReferenceEquals(minimized.SenderAvatar, first.SenderAvatar),
            "Minimized messages preserve the cached portrait without capturing the desktop");
        notifications.Clear();
        Volatile.Write(ref enabled, 0); listener.Refresh();
        Check(Wait(() => listener.Status == "已关闭" && !listener.IsConnected(fixture.Process.Id)), "Disabling the source detaches the listener");
        fixture.Send("incoming:关闭时收到的消息");
        Quiet("Messages received while disabled are not dispatched");
        Volatile.Write(ref enabled, 1); listener.Refresh();
        Check(Wait(() => listener.IsConnected(fixture.Process.Id)), "Enabling the source reconnects to the running client");
        Quiet("Re-enabling creates a silent baseline for accumulated messages");
        fixture.Send("incoming:重新开启后的消息");
        Check(Wait(() => notifications.Count == 1), "New messages after re-enabling are delivered");
        notifications.Clear();
        fixture.Send("quit");
        Check(fixture.Process.WaitForExit(5000) && Wait(() => !listener.IsConnected(fixture.Process.Id)), "Closing the client releases stale window state");
        using var reopened = FixtureClient.Start(executable);
        listener.Refresh();
        Check(Wait(() => listener.IsConnected(reopened.Process.Id)), "A restarted client is discovered automatically");
        Quiet("A restarted client does not replay history");
        reopened.Send("incoming:客户端重启后的消息");
        Check(Wait(() => notifications.Count == 1), "Messages arrive after client restart");
        using var own = Process.GetCurrentProcess();
        Check(WeChatAccessibilitySession.TryOpen(own) is null, "An unrelated executable cannot enable a WeChat accessibility flag");
        listener.Dispose(); listener.Dispose(); listener.Refresh();
        Check(!listener.IsConnected(reopened.Process.Id), "Listener cleanup is idempotent");
        Console.WriteLine($"{checks} WeChat UIAutomation checks passed.");
        return 0;

        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description + $" (status={listener.Status}, received={notifications.Count})");
            checks++; Console.WriteLine("PASS " + description);
        }
        void Quiet(string description) { Thread.Sleep(750); Check(notifications.IsEmpty, description); }
    }

    private static bool Wait(Func<bool> ready)
    {
        var time = Stopwatch.StartNew();
        while (time.Elapsed < TimeSpan.FromSeconds(8)) { if (ready()) return true; Thread.Sleep(20); }
        return false;
    }

    private static bool IsPortrait(byte[]? bytes, System.Drawing.Color expected)
    {
        if (bytes is not { Length: > 0 }) return false;
        using var stream = new MemoryStream(bytes);
        using var image = new System.Drawing.Bitmap(stream);
        var pixel = image.GetPixel(8, 8);
        return image.Size == new System.Drawing.Size(64, 64) && Math.Abs(pixel.R - expected.R) <= 5
            && Math.Abs(pixel.G - expected.G) <= 5 && Math.Abs(pixel.B - expected.B) <= 5;
    }

    private sealed class FixtureClient(Process process, NamedPipeClientStream pipe, StreamWriter writer, string ready) : IDisposable
    {
        public Process Process { get; } = process;
        public static FixtureClient Start(string executable)
        {
            var token = Guid.NewGuid().ToString("N");
            var ready = Path.Combine(AppContext.BaseDirectory, token + ".ready");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--fixture"); start.ArgumentList.Add(token); start.ArgumentList.Add(ready);
            var process = Process.Start(start)!;
            var pipe = new NamedPipeClientStream(".", token, PipeDirection.Out);
            try
            {
                pipe.Connect(8000);
                return new(process, pipe, new StreamWriter(pipe) { AutoFlush = true }, ready);
            }
            catch { if (!process.HasExited) process.Kill(); pipe.Dispose(); process.Dispose(); throw; }
        }
        public void Send(string command) => writer.WriteLine(command);
        public void Dispose()
        {
            if (!Process.HasExited)
            {
                try { Send("quit"); } catch (IOException) { }
                if (!Process.WaitForExit(3000)) Process.Kill();
            }
            writer.Dispose(); pipe.Dispose(); Process.Dispose();
            if (File.Exists(ready)) File.Delete(ready);
        }
    }
}
