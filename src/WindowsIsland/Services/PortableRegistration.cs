using System.Diagnostics;

namespace WindowsIsland.Services;

internal static class PortableRegistration
{
    public static void ScheduleRemoval()
    {
        if (!InstallationMode.IsPortable()) throw new InvalidOperationException("Not a portable installation.");
        var script = Path.Combine(AppContext.BaseDirectory, "Unregister-Package.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("Package cleanup is missing.");
        using var current = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass",
            "-File", script, "-InstallDirectory", Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
            "-ParentId", current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), "-ParentStartedUtcTicks",
            current.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        using var worker = Process.Start(start) ?? throw new IOException("Package cleanup did not start.");
    }
}
