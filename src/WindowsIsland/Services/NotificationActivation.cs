using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowsIsland.Services;

internal static class NotificationActivation
{
    public static Task<bool> OpenAsync(IslandNotification notification) => Task.Run(() => Open(notification));

    private static bool Open(IslandNotification notification)
    {
        if (notification.Source == NotificationSource.Power) return false;
        if (notification.OriginProcessId is > 0 && ShowProcess(notification.OriginProcessId.Value)) return true;
        var identity = MessengerIdentity.FromNotification(notification);
        var names = identity switch
        {
            "wechat" => new[] { "Weixin", "WeChat" },
            "telegram" => new[] { "Telegram" },
            "qq" => new[] { "QQ" },
            _ => []
        };
        foreach (var name in names)
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                    if (ShowProcess(process.Id)) return true;

        if (string.IsNullOrWhiteSpace(notification.AppId) || notification.AppId is "wechat" or "telegram" or "qq") return false;
        try { AppActivation.Launch(notification.AppId, ""); return true; }
        catch (Exception error) when (error is COMException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Trace.WriteLine($"Notification activation: {error.GetType().Name}");
            return false;
        }
    }

    private static bool ShowProcess(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            nint candidate = 0;
            var mainWindow = process.MainWindowHandle;
            var bestRank = -1;
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var owner);
                if (owner != id || GetWindow(window, 4) != 0 || (GetWindowLongPtr(window, -20).ToInt64() & 0x80) != 0) return true;
                var style = GetWindowLongPtr(window, -16).ToInt64();
                if ((style & 0x00C40000) == 0) return true;
                var className = new StringBuilder(256);
                GetClassName(window, className, className.Capacity);
                if (className.ToString() is "#32768" or "#32770") return true;
                var rank = ((style & 0x00040000) != 0 ? 8 : 0)
                    + (window == mainWindow ? 4 : 0) + (IsWindowVisible(window) ? 2 : 0);
                if (rank > bestRank) { candidate = window; bestRank = rank; }
                return true;
            }, 0);
            if (candidate == 0) return false;
            ShowWindowAsync(candidate, IsIconic(candidate) ? 9 : 5);
            return SetForegroundWindow(candidate) || GetForegroundWindow() == candidate;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Trace.WriteLine($"Notification window: {error.GetType().Name}");
            return false;
        }
    }

    private delegate bool EnumCallback(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
