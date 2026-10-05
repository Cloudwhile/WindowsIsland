using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text;
using Microsoft.Win32;

namespace WindowsIsland.Services;

internal sealed record UpdateJob(string InstallDirectory, string PayloadDirectory, string? InstallerPath,
    int ProcessId, long ProcessStartedUtcTicks, string Version, string MutexName = @"Local\WindowsIsland.Tray");
internal sealed record UpdateResult(string InstallDirectory, string Version, bool Success, string Message);

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
            WorkingDirectory = update.Directory
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-JobPath", jobPath })
            start.ArgumentList.Add(argument);
        using var helper = Process.Start(start) ?? throw new IOException("更新程序未能启动，请重试。");
        var ready = Path.Combine(update.Directory, "ready");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (!File.Exists(ready))
            {
                if (helper.HasExited) throw new IOException("更新程序未能准备就绪，请重试。");
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await File.WriteAllTextAsync(Path.Combine(update.Directory, "commit"), "", cancellationToken).ConfigureAwait(false);
            update.HandOff();
        }
        catch
        {
            try { if (!helper.HasExited) helper.Kill(); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { Trace.WriteLine(error); }
            throw;
        }
    }

    private static bool SameDirectory(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        StringComparison.OrdinalIgnoreCase);
}
