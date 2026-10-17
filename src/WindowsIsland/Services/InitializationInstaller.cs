using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;
using Windows.Management.Deployment;

namespace WindowsIsland.Services;

/// <summary>
/// Performs package preparation owned by the installer. The normal startup path
/// never invokes this command.
/// </summary>
internal static class InitializationInstaller
{
    private const string SideLoadingKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock";
    private const string SideLoadingValue = "AllowAllTrustedApps";

    internal static bool TryHandle(string[] arguments, out int result)
    {
        result = 0;
        if (arguments.Length == 0 || arguments[0] is not ("--install-initialization" or "--install-initialization-elevated"))
            return false;

        result = 2;
        if (arguments.Length != 1 || !OperatingSystem.IsWindows()) return true;
        try
        {
            if (arguments[0] == "--install-initialization-elevated")
            {
                if (!IsAdministrator()) return true;
                EnsureSideLoadingPolicy();
                InitializationPreparation.Prepare(AppContext.BaseDirectory, null);
                result = 0;
                return true;
            }

            InstallForCurrentUser(AppContext.BaseDirectory);
            result = 0;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        { result = 1223; }
        catch (OperationCanceledException)
        { result = 1223; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.Security.SecurityException or System.Xml.XmlException)
        { result = 1; }
        return true;
    }

    internal static ProcessStartInfo CreateElevationRequest()
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        info.ArgumentList.Add("--install-initialization-elevated");
        return info;
    }

    private static void InstallForCurrentUser(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (!IsSideLoadingEnabled())
        {
            if (!IsAdministrator())
            {
                using var elevated = Process.Start(CreateElevationRequest())
                    ?? throw new IOException("Initialization installer did not start.");
                elevated.WaitForExit();
                if (elevated.ExitCode != 0)
                    throw new IOException("Initialization installer could not enable package sideloading.");
            }
            else
            {
                EnsureSideLoadingPolicy();
            }
        }

        try
        {
            InitializationPreparation.Prepare(directory, null);
        }
        catch (UnauthorizedAccessException) when (!IsAdministrator())
        {
            // Only the fixed registration files need an elevated write. Package
            // registration remains in this original user's process below.
            InitializationElevation.PrepareAsync(null).GetAwaiter().GetResult();
        }

        var manifestPath = Path.Combine(directory, "AppxManifest.xml");
        var manifest = System.Xml.Linq.XDocument.Load(manifestPath);
        var name = (string?)manifest.Root?.Element(manifest.Root.Name.Namespace + "Identity")?.Attribute("Name")
            ?? throw new InvalidDataException("Missing application package name.");
        var manager = new PackageManager();
        var operation = manager.RegisterPackageAsync(new Uri(manifestPath), null, DeploymentOptions.DevelopmentMode);
        var registration = operation.AsTask().WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        if (registration.ExtendedErrorCode is not null) throw registration.ExtendedErrorCode;
        if (!manager.FindPackagesForUser(string.Empty).Any(package => package.Id.Name == name
            && SameDirectory(package.InstalledLocation.Path, directory)))
            throw new InvalidOperationException("Package registration did not finish.");
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool IsSideLoadingEnabled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(SideLoadingKey, writable: false);
        return key?.GetValue(SideLoadingValue) is int value && value == 1;
    }

    private static void EnsureSideLoadingPolicy()
    {
        using var key = Registry.LocalMachine.CreateSubKey(SideLoadingKey, writable: true)
            ?? throw new UnauthorizedAccessException("Unable to open the package sideloading policy.");
        if (key.GetValue(SideLoadingValue) is int value && value == 1) return;
        key.SetValue(SideLoadingValue, 1, RegistryValueKind.DWord);
    }

    private static bool SameDirectory(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        StringComparison.OrdinalIgnoreCase);
}
