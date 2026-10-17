using System.Diagnostics;
using System.Runtime.Versioning;
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
        if (update.InstallerPath is not { } directInstaller || !File.Exists(directInstaller)
            || !IsInstallerFileName(Path.GetFileName(directInstaller)))
            throw Localization.DataError("UpdateErrorInstallerMissing");
        cancellationToken.ThrowIfCancellationRequested();
        var installerStart = new ProcessStartInfo(directInstaller)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(directInstaller)!,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        // Preserve the active installation location when migrating from MSI or
        // when the user selected a non-default Inno directory.
        installerStart.ArgumentList.Add("/DIR=" + Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        foreach (var argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" })
            installerStart.ArgumentList.Add(argument);
        using var installerProcess = Process.Start(installerStart)
            ?? throw Localization.IoError("UpdateErrorHelperStart");
        update.HandOff();
        await Task.CompletedTask.ConfigureAwait(false);
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
        const string suffix = "-win-x64.exe";
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

    private static bool SameDirectory(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        StringComparison.OrdinalIgnoreCase);

}
