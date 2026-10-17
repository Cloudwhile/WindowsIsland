using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace WindowsIsland.Services;

internal static class UpdateElevation
{
    private const string Command = "--apply-update-worker";

    public static ProcessStartInfo Request(string executable, string jobPath)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        info.ArgumentList.Add(Command);
        info.ArgumentList.Add(jobPath);
        return info;
    }

    public static async Task StartWorkerAsync(string jobPath, string workDirectory, CancellationToken cancellationToken)
    {
        try
        {
            using var bootstrap = Process.Start(Request(Environment.ProcessPath!, jobPath))
                ?? throw new IOException("Elevated update bootstrap did not start.");
            var ready = Path.Combine(workDirectory, "elevated-ready");
            var failed = Path.Combine(workDirectory, "elevation-failed");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                while (!File.Exists(ready))
                {
                    if (File.Exists(failed) || bootstrap.HasExited && bootstrap.ExitCode != 0)
                        throw new IOException("Elevated update worker did not start.");
                    await Task.Delay(100, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException("Elevated update worker did not become ready."); }
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Update permission request was cancelled.", error, cancellationToken);
        }
    }

    public static bool TryHandle(string[] arguments, out int result)
    {
        result = 0;
        if (arguments.Length == 0 || arguments[0] != Command) return false;
        result = 2;
        string? workDirectory = null;
        try
        {
            if (!OperatingSystem.IsWindows() || arguments.Length != 2) return true;
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return true;
            var jobPath = Path.GetFullPath(arguments[1]);
            workDirectory = Path.GetDirectoryName(jobPath);
            if (workDirectory is null || Path.GetFileName(jobPath) != "job.json"
                || !Guid.TryParseExact(Path.GetFileName(workDirectory), "N", out _)) return true;
            var job = JsonSerializer.Deserialize<UpdateJob>(File.ReadAllText(jobPath));
            if (job is null || !job.RequiresElevation || !SameDirectory(job.InstallDirectory, AppContext.BaseDirectory)
                || !SameDirectory(job.PayloadDirectory, Path.Combine(workDirectory, "payload"))
                || job.InstallerPath is { } installer && !SameFile(installer, Path.Combine(workDirectory, "update.msi"))) return true;
            using var parent = Process.GetProcessById(job.ProcessId);
            if (parent.Id == Environment.ProcessId || parent.StartTime.ToUniversalTime().Ticks != job.ProcessStartedUtcTicks
                || !SameFile(parent.MainModule?.FileName, Environment.ProcessPath)) return true;
            var scriptPath = Path.Combine(AppContext.BaseDirectory, "Updater", "Apply-Update.ps1");
            if (!File.Exists(scriptPath)) return true;
            var failurePath = Path.Combine(workDirectory, "elevation-failed");
            var command = "try{$source=[IO.File]::ReadAllText(" + Literal(scriptPath)
                + ",[Text.Encoding]::UTF8);& ([ScriptBlock]::Create($source)) -JobPath " + Literal(jobPath)
                + " -ElevatedWorker -BootstrapId " + Environment.ProcessId + ";}catch{[IO.File]::WriteAllText(" + Literal(failurePath)
                + ",$_.Exception.GetType().Name);throw}";
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = workDirectory
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass",
                "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) }) start.ArgumentList.Add(argument);
            using var worker = Process.Start(start) ?? throw new IOException("Elevated update worker did not start.");
            result = 0;
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or InvalidOperationException or System.Security.SecurityException)
        {
            if (workDirectory is not null)
            {
                try { File.WriteAllText(Path.Combine(workDirectory, "elevation-failed"), error.GetType().Name); }
                catch { }
            }
            result = 1;
        }
        return true;
    }

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static bool SameDirectory(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        StringComparison.OrdinalIgnoreCase);
    private static bool SameFile(string? left, string? right) => left is not null && right is not null
        && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
