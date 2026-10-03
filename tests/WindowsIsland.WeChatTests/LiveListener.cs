using System.Diagnostics;
using System.IO;
using WindowsIsland.Services;

internal static class LiveListener
{
    public static int Run()
    {
        var arrivals = 0;
        var directory = Process.GetProcessesByName("Weixin").Select(process =>
        {
            using (process) { try { return Path.GetDirectoryName(process.MainModule?.FileName); } catch { return null; } }
        }).FirstOrDefault(path => path is not null);
        if (directory is null) { Console.WriteLine("LIVE no running Weixin client"); return 1; }
        using var service = new WeChatService(() => true, action => action(), directory);
        service.Received += notification =>
        {
            Interlocked.Increment(ref arrivals);
            Console.WriteLine($"ARRIVAL titleChars={notification.Title.Length} bodyChars={notification.Body.Length} appIcon={notification.AppIcon is { Length: > 0 }}");
        };
        service.Start();
        var clock = Stopwatch.StartNew();
        var status = "";
        var connected = false;
        while (clock.Elapsed < TimeSpan.FromSeconds(45))
        {
            if (status != service.Status)
            {
                status = service.Status;
                connected |= status == "监听中";
                Console.WriteLine("STATUS " + status);
            }
            Thread.Sleep(100);
        }
        Console.WriteLine($"LIVE connected={connected} arrivals={Volatile.Read(ref arrivals)}");
        return connected ? 0 : 1;
    }
}
