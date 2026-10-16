using System.Collections.Concurrent;
using System.IO;
using WindowsIsland.Services;

internal static class AccessibilityRecoveryChecks
{
    public static int Run()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var module = Path.Combine(directory, "Weixin.dll");
        AccessibilityChecks.WriteFixtureModule(module);
        Diagnostics.Initialize(Path.Combine(directory, "diagnostics"), true);
        var checks = 0;
        try
        {
            var notifications = new ConcurrentQueue<IslandNotification>();
            using var listener = new WeChatService(() => true, action => action(), AppContext.BaseDirectory);
            listener.Received += notifications.Enqueue;
            listener.Start();
            Check(Wait(() => listener.StatusKey == "NotRunning"), "An unopened WeChat client is reported as not running");
            Thread.Sleep(700);
            Check(Diagnostics.Store!.Snapshot().Length == 0, "An unopened WeChat client does not create accessibility errors");
            var executable = Path.Combine(AppContext.BaseDirectory, "WeChat.exe");
            File.Copy(Environment.ProcessPath!, executable, true);
            using var fixture = ListenerChecks.FixtureClient.Start(executable, module);
            Check(Wait(() => Recorded("AccessibilityUnavailable")), "The first unavailable accessibility attempt is recorded without message content");
            fixture.Send("accessibility-load");
            Check(Wait(() => listener.IsConnected(fixture.Process.Id)), "A failed first attempt retries when the client module becomes available");
            Thread.Sleep(700);
            Check(notifications.IsEmpty, "Delayed accessibility creates a silent baseline instead of replaying history");
            fixture.Send("incoming:辅助访问开启后的消息");
            Check(Wait(() => notifications.Count == 1), "The recovered client delivers new messages");
            notifications.Clear();
            fixture.Send("accessibility-reset");
            Check(Wait(() => Recorded("AccessibilityRestored")), "A running client resetting the accessibility gate is detected and restored");
            Check(Wait(() => listener.IsConnected(fixture.Process.Id)), "The listener reconnects after gate restoration");
            Thread.Sleep(700);
            Check(notifications.IsEmpty, "Gate restoration does not replay previous messages");
            fixture.Send("incoming:开关恢复后的消息");
            Check(Wait(() => notifications.Count == 1), "Messages continue after gate recovery without restarting the application");
            var beforeClose = Diagnostics.Store!.Snapshot().Sum(entry => entry.Count);
            fixture.Send("quit");
            Check(fixture.Process.WaitForExit(5000) && Wait(() => listener.StatusKey == "NotRunning"), "Closing WeChat returns to the normal not-running state");
            Thread.Sleep(700);
            Check(Diagnostics.Store.Snapshot().Sum(entry => entry.Count) == beforeClose, "Closing WeChat does not create accessibility errors");
            listener.Dispose();
            Console.WriteLine($"{checks} WeChat accessibility recovery checks passed.");
            return 0;
        }
        finally { Diagnostics.Shutdown(); }

        bool Recorded(string operation) => Diagnostics.Store!.Snapshot().Any(entry => entry.Operation == operation);
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++; Console.WriteLine("PASS " + message);
        }
    }

    private static bool Wait(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(18);
        while (DateTime.UtcNow < deadline) { if (ready()) return true; Thread.Sleep(50); }
        return false;
    }
}
