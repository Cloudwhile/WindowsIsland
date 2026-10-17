using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace WindowsIsland.Services;

internal sealed record UpdateJob(string InstallDirectory, string PayloadDirectory, string? InstallerPath,
    string? InstallerSourcePath, int ProcessId, long ProcessStartedUtcTicks, string Version,
    string MutexName = @"Local\WindowsIsland.Tray",
    bool RestoreRegistration = false, bool RequiresElevation = false, string UserSid = "");
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
            return ValidateResult(result, directory ?? AppContext.BaseDirectory, ReleaseVersion.Current);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    internal static UpdateResult? ValidateResult(UpdateResult? result, string directory, ReleaseVersion current)
    {
        if (result is null || !SameDirectory(result.InstallDirectory, directory)
            || !ReleaseVersion.TryParse(result.Version, out var target) || target!.CompareTo(current) < 0) return null;
        if (result.Success && target.CompareTo(current) != 0)
            return result with { Success = false, Message = "", MessageKey = "UpdateFailed" };
        return result with { Version = target.Display };
    }

    public static async Task StartAsync(PreparedUpdate update, CancellationToken cancellationToken,
        string mutexName = @"Local\WindowsIsland.Tray", bool restoreRegistration = false)
    {
        using var application = Process.GetCurrentProcess();
        var directory = Path.GetFullPath(AppContext.BaseDirectory);
        var workDirectory = UpdateWorkDirectory.Resolve(update.Directory);
        var payload = UpdateWorkDirectory.Rebase(update.StagingDirectory, update.Directory, workDirectory);
        var installer = update.InstallerPath is { } path ? UpdateWorkDirectory.Rebase(path, update.Directory, workDirectory) : null;
        var installerSource = installer is null ? null : StageInstallerSource(installer, workDirectory);
        var requiresElevation = RequiresElevation(directory);
        var userSid = OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User?.Value ?? "" : "";
        var job = new UpdateJob(directory, payload, installer, installerSource,
            application.Id, application.StartTime.ToUniversalTime().Ticks, update.Version, mutexName, restoreRegistration,
            requiresElevation, userSid);
        var jobPath = Path.Combine(workDirectory, "job.json");
        await File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(job), cancellationToken).ConfigureAwait(false);
        var scriptPath = Path.Combine(workDirectory, "Apply-Update.ps1");
        // Windows PowerShell 5.1 needs a BOM to read Chinese strings in script files.
        var script = await File.ReadAllTextAsync(Path.Combine(directory, "Updater", "Apply-Update.ps1"), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        if (requiresElevation)
            await UpdateElevation.StartWorkerAsync(jobPath, workDirectory, cancellationToken).ConfigureAwait(false);
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = workDirectory, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-JobPath", jobPath })
            start.ArgumentList.Add(argument);
        var helper = Process.Start(start) ?? throw Localization.IoError("UpdateErrorHelperStart");
        // The read task owns the process streams until EOF, including after handoff.
        var errors = ReadErrorsAsync(helper.StandardError);
        var ready = Path.Combine(workDirectory, "ready");
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
            await File.WriteAllTextAsync(Path.Combine(workDirectory, "commit"), "", cancellationToken).ConfigureAwait(false);
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
        finally { _ = ReleaseHelperAsync(helper, errors); }
    }

    internal static bool RequiresElevation(string directory)
    {
        var probe = Path.Combine(Path.GetFullPath(directory), ".island-access-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.DeleteOnClose);
            return false;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return true;
        }
    }

    internal static bool IsInstallerFileName(string? name)
    {
        const string prefix = "WindowsIsland-";
        const string suffix = "-win-x64.msi";
        if (name is null || !name.StartsWith(prefix, StringComparison.Ordinal)
            || !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        var version = name[prefix.Length..^suffix.Length];
        return ReleaseVersion.TryParse(version, out _);
    }

    internal static bool IsInstallerSourcePath(string? path, string workDirectory)
    {
        if (path is null) return false;
        var full = Path.GetFullPath(path);
        var sourceRoot = Path.GetDirectoryName(Path.GetFullPath(workDirectory));
        return sourceRoot is not null
            && SameDirectory(Path.GetDirectoryName(full) ?? "", sourceRoot)
            && IsInstallerFileName(Path.GetFileName(full));
    }

    private static string StageInstallerSource(string installer, string workDirectory)
    {
        var name = Path.GetFileName(installer);
        if (!IsInstallerFileName(name)) throw Localization.DataError("UpdateErrorInstallerMissing");
        var sourceRoot = Path.GetDirectoryName(Path.GetFullPath(workDirectory))
            ?? throw Localization.IoError("UpdateErrorWorkDir");
        var source = Path.Combine(sourceRoot, name);
        if (!SameFile(installer, source)) File.Copy(installer, source, overwrite: true);
        return source;
    }

    private static async Task ReleaseHelperAsync(Process helper, Task<string> errors)
    {
        using var process = helper;
        try { await errors.ConfigureAwait(false); }
        catch (Exception error) { Trace.WriteLine(error); }
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
        catch (Exception error) when (error is IOException or ObjectDisposedException) { Diagnostics.Capture(DiagnosticArea.Updates, error); Trace.WriteLine(error); }
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
            var log = $"{DateTimeOffset.UtcNow:O} · {ReleaseVersion.FromInformationalVersion(update.Version).Tag} · {Path.GetFileName(update.Directory)}{Environment.NewLine}{error.Message}{Environment.NewLine}{detail}";
            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(update.Directory)!, "last-helper-error.log"), log, Encoding.UTF8).ConfigureAwait(false);
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException or TimeoutException or System.Security.SecurityException) { Trace.WriteLine(logError); }
    }

    private static bool SameDirectory(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        StringComparison.OrdinalIgnoreCase);

    private static bool SameFile(string left, string right) => string.Equals(
        Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
