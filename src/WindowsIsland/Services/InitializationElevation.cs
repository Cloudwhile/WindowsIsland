using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;

namespace WindowsIsland.Services;

internal static class InitializationElevation
{
    public static ProcessStartInfo Request(string executable, int parent, long started, Version? version)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add("--prepare-initialization");
        info.ArgumentList.Add(parent.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(started.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(version?.ToString(4) ?? "none");
        return info;
    }

    public static async Task PrepareAsync(Version? version)
    {
        using var parent = Process.GetCurrentProcess();
        try
        {
            using var helper = Process.Start(Request(Environment.ProcessPath!, parent.Id,
                parent.StartTime.ToUniversalTime().Ticks, version)) ?? throw new IOException("Initialization helper did not start.");
            await helper.WaitForExitAsync();
            if (helper.ExitCode != 0) throw new IOException("Initialization helper could not prepare registration files.");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        { throw new OperationCanceledException("Initialization permission request was cancelled.", error); }
    }

    public static bool TryHandle(string[] arguments, out int result)
    {
        result = 0;
        if (arguments.Length == 0 || arguments[0] != "--prepare-initialization") return false;
        result = 2;
        if (!OperatingSystem.IsWindows() || arguments.Length != 4
            || !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parentId) || parentId <= 0
            || !long.TryParse(arguments[2], NumberStyles.None, CultureInfo.InvariantCulture, out var started) || started <= 0) return true;
        Version? version = null;
        if (arguments[3] != "none" && (!Version.TryParse(arguments[3], out version)
            || version.Major > ushort.MaxValue || version.Minor > ushort.MaxValue
            || version.Build is < 0 or > ushort.MaxValue || version.Revision is < 0 or > ushort.MaxValue)) return true;
        try
        {
            using var current = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator)) return true;
            using var parent = Process.GetProcessById(parentId);
            if (parent.Id == Environment.ProcessId || parent.StartTime.ToUniversalTime().Ticks != started
                || !string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return true;
            // No caller-supplied paths or contents, and no per-user registration here.
            InitializationPolicy.EnsureEnabled();
            InitializationPreparation.Prepare(AppContext.BaseDirectory, version);
            result = 0;
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.Security.SecurityException or System.Xml.XmlException) { result = 1; }
        return true;
    }
}
