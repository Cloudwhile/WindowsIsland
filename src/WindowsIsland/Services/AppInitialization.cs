using System.Runtime.InteropServices;
using System.Xml.Linq;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace WindowsIsland.Services;

internal static class AppInitialization
{
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

    public static Task<string> InitializeAsync(bool force = false) => Task.Run(() => InitializeCoreAsync(force));

    private static async Task<string> InitializeCoreAsync(bool force)
    {
        if (!force && HasIdentity) return Package.Current.Id.FamilyName + "!App";
        var directory = Path.GetFullPath(AppContext.BaseDirectory);
        var template = XDocument.Load(Path.Combine(directory, "Initialization", "AppxManifest.xml"));
        var name = PackageName(template);
        var manager = new PackageManager();
        var installed = manager.FindPackagesForUser(string.Empty).FirstOrDefault(package => package.Id.Name == name);
        if (!force && installed is not null && SameLocation(installed, directory)) return installed.Id.FamilyName + "!App";
        var installedVersion = installed?.Id.Version;
        var version = installedVersion is { } value ? new Version(value.Major, value.Minor, value.Build, value.Revision) : null;
        var manifest = InitializationManifest.Build(
            template,
            XDocument.Load(Path.Combine(directory, "Initialization", "RuntimeManifest.xml")),
            path => File.Exists(Path.Combine(directory, path)), version);
        InitializationResources.Prepare(Path.Combine(directory, "WindowsIsland.pri"), Path.Combine(directory, "resources.pri"));
        var manifestPath = Path.Combine(directory, "AppxManifest.xml");
        manifest.Save(manifestPath);
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
