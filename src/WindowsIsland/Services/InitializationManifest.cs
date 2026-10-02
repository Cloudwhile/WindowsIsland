using System.Xml.Linq;

namespace WindowsIsland.Services;

internal static class InitializationManifest
{
    public static XDocument Build(XDocument template, XDocument runtime, Func<string, bool> exists, Version? installedVersion = null)
    {
        var document = new XDocument(template);
        var package = document.Root ?? throw new InvalidDataException("Missing application package manifest.");
        var ns = package.Name.Namespace;
        var identity = package.Element(ns + "Identity") ?? throw new InvalidDataException("Missing package identity.");
        var version = Version.Parse((string?)identity.Attribute("Version") ?? "1.0.0.0");
        if (installedVersion is not null && installedVersion >= version)
        {
            if (installedVersion.Revision >= ushort.MaxValue) throw new InvalidDataException("Package version exhausted.");
            identity.SetAttributeValue("Version", new Version(installedVersion.Major, installedVersion.Minor,
                installedVersion.Build, installedVersion.Revision + 1));
        }
        package.Element(ns + "Extensions")?.Remove();
        var extensions = new XElement(ns + "Extensions");
        foreach (var extension in runtime.Root?.Element(ns + "Extensions")?.Elements() ?? [])
        {
            var category = (string?)extension.Attribute("Category");
            if (category is not ("windows.activatableClass.inProcessServer" or "windows.activatableClass.proxyStub")) continue;
            var path = (string?)extension.Element(ns + "InProcessServer")?.Element(ns + "Path");
            if (path is not null && !exists(path)) continue;
            extensions.Add(new XElement(extension));
        }
        var capabilities = package.Element(ns + "Capabilities") ?? throw new InvalidDataException("Missing capabilities.");
        capabilities.AddBeforeSelf(extensions);
        return document;
    }
}
