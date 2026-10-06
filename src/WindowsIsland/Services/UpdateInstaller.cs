using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace WindowsIsland.Services;

internal sealed record UpdateJob(string InstallDirectory, string PayloadDirectory, string? InstallerPath,
    int ProcessId, long ProcessStartedUtcTicks, string Version, string MutexName = @"Local\WindowsIsland.Tray");
internal sealed record UpdateResult(string InstallDirectory, string Version, bool Success, string Message, string? MessageKey = null);

internal static class UpdateInstaller
{
    [SupportedOSPlatform("windows")]
    public static bool IsMsiInstallation(string? directory = null)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Cloudwhile\WindowsIsland\Installer");
        return key?.GetValue("InstallDirectory") is string installed && SameDirectory(installed, directory ?? AppContext.BaseDirectory);
    }

    public static UpdateResult? ReadResult(string? directory = null)
    {
        try
        {
            var result = JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(Path.Combine(UpdatePackage.WorkRoot, "result.json")));
            return result is not null && SameDirectory(result.InstallDirectory, directory ?? AppContext.BaseDirectory) ? result : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    public static async Task StartAsync(PreparedUpdate update, CancellationToken cancellationToken,
        string mutexName = @"Local\WindowsIsland.Tray")
    {
        using var application = Process.GetCurrentProcess();
        var directory = Path.GetFullPath(AppContext.BaseDirectory);
        var job = new UpdateJob(directory, update.StagingDirectory, update.InstallerPath,
            application.Id, application.StartTime.ToUniversalTime().Ticks, update.Version, mutexName);
        var jobPath = Path.Combine(update.Directory, "job.json");
        await File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(job), cancellationToken).ConfigureAwait(false);
        var scriptPath = Path.Combine(update.Directory, "Apply-Update.ps1");
        // Windows PowerShell 5.1 needs a BOM to read Chinese strings in script files.
        var script = await File.ReadAllTextAsync(Path.Combine(directory, "Updater", "Apply-Update.ps1"), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = update.Directory, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-JobPath", jobPath })
            start.ArgumentList.Add(argument);
        using var helper = Process.Start(start) ?? throw Localization.IoError("UpdateErrorHelperStart");
        var errors = ReadErrorsAsync(helper.StandardError);
        var ready = Path.Combine(update.Directory, "ready");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            while (!File.Exists(ready))
            {
                if (helper.HasExited) throw Localization.IoError("UpdateErrorHelperReady");
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await File.WriteAllTextAsync(Path.Combine(update.Directory, "commit"), "", cancellationToken).ConfigureAwait(false);
            update.HandOff();
        }
        catch (Exception error)
        {
            try
            {
                if (!helper.HasExited) helper.Kill();
                using var stopped = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await helper.WaitForExitAsync(stopped.Token).ConfigureAwait(false);
            }
            catch (Exception stopError) when (stopError is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException) { Trace.WriteLine(stopError); }
            await RecordFailureAsync(update, error, errors).ConfigureAwait(false);
            if (error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                throw Localization.IoError("UpdateErrorHelperTimeout");
            throw;
        }
    }

    private static async Task<string> ReadErrorsAsync(StreamReader reader)
    {
        using var errorReader = reader;
        var text = new StringBuilder();
        var buffer = new char[1024];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                text.Append(buffer, 0, Math.Min(read, Math.Max(0, 8192 - text.Length)));
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { Trace.WriteLine(error); }
        return text.ToString();
    }

    private static async Task RecordFailureAsync(PreparedUpdate update, Exception error, Task<string> errors)
    {
        try
        {
            var detail = "";
            try { detail = await errors.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (TimeoutException timeoutError) { Trace.WriteLine(timeoutError); }
            var scriptLog = Path.Combine(update.Directory, "error.log");
            if (File.Exists(scriptLog))
            {
                using var reader = File.OpenText(scriptLog);
                var buffer = new char[8192];
                var read = await reader.ReadAsync(buffer).ConfigureAwait(false);
                detail += Environment.NewLine + new string(buffer, 0, read);
            }
            var log = $"{DateTimeOffset.UtcNow:O} · v{update.Version} · {Path.GetFileName(update.Directory)}{Environment.NewLine}{error.Message}{Environment.NewLine}{detail}";
            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(update.Directory)!, "last-helper-error.log"), log, Encoding.UTF8).ConfigureAwait(false);
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException or TimeoutException or System.Security.SecurityException) { Trace.WriteLine(logError); }
    }

    private static bool SameDirectory(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        StringComparison.OrdinalIgnoreCase);
}
