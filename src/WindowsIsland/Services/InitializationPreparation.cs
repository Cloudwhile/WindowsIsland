using System.Xml.Linq;

namespace WindowsIsland.Services;

internal static class InitializationPreparation
{
    public static async Task PrepareAsync(string directory, Version? version, Func<Version?, Task> elevate)
    {
        try { Prepare(directory, version); }
        catch (UnauthorizedAccessException)
        {
            // Only the registration files need access to the installation.
            // Register the package later in the requesting user's context.
            await elevate(version);
            if (!XNode.DeepEquals(Manifest(directory, version), XDocument.Load(Path.Combine(directory, "AppxManifest.xml")))
                || !InitializationResources.Matches(Path.Combine(directory, "WindowsIsland.pri"), Path.Combine(directory, "resources.pri")))
                throw new IOException("Initialization files were not prepared correctly.");
        }
    }

    public static void Prepare(string directory, Version? version)
    {
        var manifest = Manifest(directory, version);
        InitializationResources.Prepare(Path.Combine(directory, "WindowsIsland.pri"), Path.Combine(directory, "resources.pri"));
        var destination = Path.Combine(directory, "AppxManifest.xml");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            manifest.Save(temporary);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static XDocument Manifest(string directory, Version? version) => InitializationManifest.Build(
        XDocument.Load(Path.Combine(directory, "Initialization", "AppxManifest.xml")),
        XDocument.Load(Path.Combine(directory, "Initialization", "RuntimeManifest.xml")),
        path => File.Exists(Path.Combine(directory, path)), version);
}
