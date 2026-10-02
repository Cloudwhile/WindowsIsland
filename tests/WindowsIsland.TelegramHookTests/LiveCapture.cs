using System.Diagnostics;
using WindowsIsland.Services;

internal static class LiveCapture
{
    public static async Task RunAsync(string dll)
    {
        using var client = Process.GetProcessesByName("Telegram").Single();
        var message = new TaskCompletionSource<IslandNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var session = new TelegramHookSession(client.Id, notification => message.TrySetResult(notification)))
        {
            await session.AttachAsync(dll);
            Console.WriteLine("PASS: Verified Telegram executable and connected the native DLL bridge");
            var notification = await message.Task.WaitAsync(TimeSpan.FromSeconds(60));
            if (string.IsNullOrWhiteSpace(notification.Body) || string.IsNullOrWhiteSpace(notification.Title))
                throw new InvalidOperationException("The actual Telegram notification did not supply title and body.");
            Console.WriteLine($"PASS: Actual Telegram popup supplied original UTF-16 title ({notification.Title.Length} chars) and body ({notification.Body.Length} chars)");
            if (notification.SenderAvatar is not { Length: > 0 }) throw new InvalidOperationException("Telegram avatar buffer was unavailable.");
            Console.WriteLine($"PASS: The actual popup supplied its avatar image ({notification.SenderAvatar.Length} PNG bytes)");
        }
        var timeout = Stopwatch.StartNew();
        bool Loaded()
        {
            client.Refresh();
            return client.Modules.Cast<ProcessModule>().Any(module => module.ModuleName == "WindowsIsland.TelegramHook.dll");
        }
        while (Loaded() && timeout.Elapsed.TotalSeconds < 4) await Task.Delay(50);
        if (Loaded() || client.HasExited) throw new InvalidOperationException("Live capture did not release the DLL cleanly.");
        Console.WriteLine("PASS: Capture stopped and the DLL unloaded with Telegram still running");
    }
}
