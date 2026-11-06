using System.Diagnostics;
using System.Text.Json;
using System.ComponentModel;

namespace WindowsIsland.Services;

internal sealed record UpdateResult(string InstallDirectory, string Version, bool Success, string Message, string? MessageKey = null);

internal static class UpdateInstaller
{
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

    public static async Task StartAsync(PreparedUpdate update, CancellationToken cancellationToken)
    {
        if (update.InstallerPath is not { } directInstaller || !File.Exists(directInstaller)
            || !IsInstallerFileName(Path.GetFileName(directInstaller)))
            throw Localization.DataError("UpdateErrorInstallerMissing");
        cancellationToken.ThrowIfCancellationRequested();
        // Confirm the final privilege mode before exiting, so cancelled UAC preserves the app.
        var eventName = @"Local\WindowsIsland.Update." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        var installerStart = CreateStartInfo(directInstaller, AppContext.BaseDirectory);
        installerStart.ArgumentList.Add("/UPDATEEVENT=" + eventName);
        try
        {
            using var installerProcess = Process.Start(installerStart)
                ?? throw Localization.IoError("UpdateErrorHelperStart");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            while (!ready.WaitOne(0))
            {
                if (installerProcess.HasExited)
                    throw new OperationCanceledException("Installer did not accept the update.", cancellationToken);
                await Task.Delay(50, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        { throw new OperationCanceledException("Update permission request was cancelled.", error, cancellationToken); }
        update.HandOff();
    }

    internal static ProcessStartInfo CreateStartInfo(string installer, string directory)
    {
        var info = new ProcessStartInfo(installer)
        {
            UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(installer)!,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        info.ArgumentList.Add("/DIR=" + Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)));
        foreach (var argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/UPDATE=1" })
            info.ArgumentList.Add(argument);
        if (InstallationMode.IsPortable(directory))
        {
            info.ArgumentList.Add("/PORTABLE=1");
            info.ArgumentList.Add("/CURRENTUSER");
        }
        else info.ArgumentList.Add("/ALLUSERS");
        return info;
    }

    internal static bool IsInstallerFileName(string? name)
    {
        return name is { Length: > 4 and <= 255 } && Path.GetFileName(name) == name
            && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !name.Contains('/') && !name.Contains('\\')
            && Path.GetExtension(name).Equals(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameDirectory(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        StringComparison.OrdinalIgnoreCase);

}
