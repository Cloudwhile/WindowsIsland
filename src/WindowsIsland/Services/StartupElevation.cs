using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace WindowsIsland.Services;

internal static class StartupElevation
{
    public static ProcessStartInfo Request(bool enabled, string executable, int parent, long started)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add("--configure-startup");
        info.ArgumentList.Add(enabled ? "enable" : "disable");
        info.ArgumentList.Add(parent.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(started.ToString(CultureInfo.InvariantCulture));
        return info;
    }

    public static async Task<bool> ConfigureAsync(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var parent = Process.GetCurrentProcess();
        var request = Request(enabled, Environment.ProcessPath!, parent.Id, parent.StartTime.ToUniversalTime().Ticks);
        try
        {
            using var process = Process.Start(request) ?? throw new IOException("The startup helper did not start.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException("The startup helper failed.");
            return true;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { return false; }
    }

    public static bool TryHandle(string[] arguments, out int result)
    {
        result = 0;
        if (arguments.Length == 0 || arguments[0] != "--configure-startup") return false;
        result = 2;
        if (!OperatingSystem.IsWindows()) return true;
        if (arguments.Length != 4 || arguments[1] is not ("enable" or "disable")
            || !int.TryParse(arguments[2], CultureInfo.InvariantCulture, out var parentId)
            || !long.TryParse(arguments[3], CultureInfo.InvariantCulture, out var started)) return true;
        try
        {
            using var current = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator)) return true;
            using var parent = Process.GetProcessById(parentId);
            if (parent.StartTime.ToUniversalTime().Ticks != started || !string.Equals(parent.MainModule?.FileName,
                Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return true;
            if (!OpenProcessToken(parent.Handle, 8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token)
            using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
            using (var hive = Registry.Users.OpenSubKey(owner.User!.Value, writable: true)
                ?? throw new IOException("The requesting user's preferences are unavailable."))
            {
                new StartupRegistration(new RegistryWindowsPreferenceStore(hive), Environment.ProcessPath!).Set(arguments[1] == "enable");
            }
            result = 0;
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.Security.SecurityException) { result = 1; }
        return true;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);
}
