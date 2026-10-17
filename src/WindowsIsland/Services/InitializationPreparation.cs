using System.Xml.Linq;

namespace WindowsIsland.Services;

internal static class InitializationPreparation
{
    public static async Task<bool> PrepareAsync(string directory, Version? version, Func<Version?, Task> elevate)
    {
        try { Prepare(directory, version); }
        catch (UnauthorizedAccessException)
        {
            // Prepare the fixed registration resources and the directory Windows
            // uses for package metadata. Registration stays with the requesting user.
            await elevate(version);
            if (!XNode.DeepEquals(Manifest(directory, version), XDocument.Load(Path.Combine(directory, "AppxManifest.xml")))
                || !InitializationResources.Matches(Path.Combine(directory, "WindowsIsland.pri"), Path.Combine(directory, "resources.pri"))
                || !Directory.Exists(Path.Combine(directory, "microsoft.system.package.metadata")))
                throw new IOException("Initialization files were not prepared correctly.");
            return true;
        }
        return false;
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
        // A normal user can register a loose package in Program Files only after
        // this directory exists. Inherit its existing protection; do not grant
        // the user write access to package metadata or application binaries.
        Directory.CreateDirectory(Path.Combine(directory, "microsoft.system.package.metadata"));
    }

    private static XDocument Manifest(string directory, Version? version) => InitializationManifest.Build(
        XDocument.Load(Path.Combine(directory, "Initialization", "AppxManifest.xml")),
        XDocument.Load(Path.Combine(directory, "Initialization", "RuntimeManifest.xml")),
        path => File.Exists(Path.Combine(directory, path)), version);
}
