using System.Drawing;
using System.Drawing.Imaging;
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
        var package = new PackageManager().FindPackagesForUser(string.Empty)
            .FirstOrDefault(package => package.Id.Name == "WindowsIsland.Desktop" && SameLocation(package, directory));
        return package is null ? null : package.Id.FamilyName + "!App";
    }

    public static async Task<string> InitializeAsync()
    {
        if (HasIdentity) return Package.Current.Id.FamilyName + "!App";
        var directory = Path.GetFullPath(AppContext.BaseDirectory);
        var manager = new PackageManager();
        var installed = manager.FindPackagesForUser(string.Empty).FirstOrDefault(package => package.Id.Name == "WindowsIsland.Desktop");
        if (installed is not null && SameLocation(installed, directory)) return installed.Id.FamilyName + "!App";
        var installedVersion = installed?.Id.Version;
        var version = installedVersion is { } value ? new Version(value.Major, value.Minor, value.Build, value.Revision) : null;
        var manifest = InitializationManifest.Build(
            XDocument.Load(Path.Combine(directory, "Initialization", "AppxManifest.xml")),
            XDocument.Load(Path.Combine(directory, "Initialization", "RuntimeManifest.xml")),
            path => File.Exists(Path.Combine(directory, path)), version);
        PrepareLogos(directory);
        File.Copy(Path.Combine(directory, "WindowsIsland.pri"), Path.Combine(directory, "resources.pri"), overwrite: true);
        var manifestPath = Path.Combine(directory, "AppxManifest.xml");
        manifest.Save(manifestPath);
        var operation = manager.RegisterPackageAsync(new Uri(manifestPath), null, DeploymentOptions.DevelopmentMode);
        var result = await operation;
        if (result.ExtendedErrorCode is not null) throw result.ExtendedErrorCode;
        var registered = manager.FindPackagesForUser(string.Empty).FirstOrDefault(package => package.Id.Name == "WindowsIsland.Desktop")
            ?? throw new InvalidOperationException("Package registration did not finish.");
        return registered.Id.FamilyName + "!App";
    }

    private static bool SameLocation(Package package, string directory) =>
        string.Equals(Path.TrimEndingDirectorySeparator(package.InstalledLocation.Path),
            Path.TrimEndingDirectorySeparator(directory), StringComparison.OrdinalIgnoreCase);

    private static void PrepareLogos(string directory)
    {
        var assets = Path.Combine(directory, "Assets");
        Directory.CreateDirectory(assets);
        using var source = new Icon(Path.Combine(assets, "Island.ico"), 256, 256);
        using var image = source.ToBitmap();
        foreach (var (name, size) in new[] { ("StoreLogo.png", 50), ("Square44x44Logo.png", 44), ("Square150x150Logo.png", 150) })
        {
            using var bitmap = new Bitmap(size, size);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(image, 0, 0, size, size);
            }
            bitmap.Save(Path.Combine(assets, name), ImageFormat.Png);
        }
    }
}
