using System.Runtime.InteropServices;
using System.Xml.Linq;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace WindowsIsland.Services;

internal static class AppInitialization
{
    private static readonly SemaphoreSlim InitializationGate = new(1, 1);
    private static long _retryAfterUtcTicks;
    private static int _failedAttempts;

    internal static bool RetryDeferred => DateTimeOffset.UtcNow.UtcTicks < Volatile.Read(ref _retryAfterUtcTicks);
    internal static int FailedAttempts => Volatile.Read(ref _failedAttempts);

    public static bool HasIdentity
    {
        get
        {
            try { _ = Package.Current.Id; return true; }
            catch (Exception error) when (error is InvalidOperationException or COMException) { return false; }
        }
    }

    public static string? RegisteredAppId()
    {
        var directory = Path.GetFullPath(AppContext.BaseDirectory);
        var name = PackageName(XDocument.Load(Path.Combine(directory, "Initialization", "AppxManifest.xml")));
        var package = new PackageManager().FindPackagesForUser(string.Empty)
            .FirstOrDefault(package => package.Id.Name == name && SameLocation(package, directory));
        return package is null ? null : package.Id.FamilyName + "!App";
    }

    public static async Task<string> InitializeAsync(bool force = false)
    {
        if (!force && RetryDeferred)
            throw new InitializationDeferredException(new DateTimeOffset(Volatile.Read(ref _retryAfterUtcTicks), TimeSpan.Zero));
        await InitializationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!force && RetryDeferred)
                throw new InitializationDeferredException(new DateTimeOffset(Volatile.Read(ref _retryAfterUtcTicks), TimeSpan.Zero));
            var result = await Task.Run(() => InitializeCoreAsync(force)).ConfigureAwait(false);
            Volatile.Write(ref _failedAttempts, 0);
            Volatile.Write(ref _retryAfterUtcTicks, 0);
            return result;
        }
        catch
        {
            var attempts = Interlocked.Increment(ref _failedAttempts);
            var seconds = Math.Min(300, Math.Pow(2, Math.Min(attempts, 8)));
            Volatile.Write(ref _retryAfterUtcTicks, DateTimeOffset.UtcNow.AddSeconds(seconds).UtcTicks);
            throw;
        }
        finally { InitializationGate.Release(); }
    }

    private static async Task<string> InitializeCoreAsync(bool force)
    {
        if (!force && HasIdentity) return Package.Current.Id.FamilyName + "!App";
        var directory = Path.GetFullPath(AppContext.BaseDirectory);
        var template = XDocument.Load(Path.Combine(directory, "Initialization", "AppxManifest.xml"));
        var name = PackageName(template);
        var manager = new PackageManager();
        // A package with the same identity can remain registered in another
        // installation directory. It must not supply this directory's version
        // or short-circuit registration.
        var installed = manager.FindPackagesForUser(string.Empty)
            .FirstOrDefault(package => package.Id.Name == name && SameLocation(package, directory));
        if (!force && installed is not null) return installed.Id.FamilyName + "!App";
        var installedVersion = installed?.Id.Version;
        var version = installedVersion is { } value ? new Version(value.Major, value.Minor, value.Build, value.Revision) : null;
        var systemPrepared = await InitializationPreparation.PrepareAsync(directory, version, InitializationElevation.PrepareAsync);
        return await InitializationRegistration.RegisterAsync(
            () => RegisterCurrentUserAsync(manager, directory, name),
            () => InitializationElevation.PrepareAsync(version), systemPrepared);
    }

    private static async Task<string> RegisterCurrentUserAsync(PackageManager manager, string directory, string name)
    {
        var manifestPath = Path.Combine(directory, "AppxManifest.xml");
        var operation = manager.RegisterPackageAsync(new Uri(manifestPath), null, DeploymentOptions.DevelopmentMode);
        DeploymentResult result;
        try { result = await operation.AsTask().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException) { operation.Cancel(); throw; }
        if (result.ExtendedErrorCode is not null) throw result.ExtendedErrorCode;
        var registered = manager.FindPackagesForUser(string.Empty).FirstOrDefault(package => package.Id.Name == name && SameLocation(package, directory))
            ?? throw new InvalidOperationException("Package registration did not finish.");
        return registered.Id.FamilyName + "!App";
    }

    private static string PackageName(XDocument manifest) =>
        (string?)manifest.Root?.Element(manifest.Root.Name.Namespace + "Identity")?.Attribute("Name")
        ?? throw new InvalidDataException("Missing application package name.");

    private static bool SameLocation(Package package, string directory) =>
        string.Equals(Path.TrimEndingDirectorySeparator(package.InstalledLocation.Path),
            Path.TrimEndingDirectorySeparator(directory), StringComparison.OrdinalIgnoreCase);

}

internal sealed class InitializationDeferredException(DateTimeOffset retryAfter)
    : IOException("Initialization retry is deferred until " + retryAfter.ToUniversalTime().ToString("O"))
{
    internal DateTimeOffset RetryAfter { get; } = retryAfter;
}
