using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

internal sealed record PluginConsentDescriptor(string PluginId, string Name, string Version,
    string DeveloperId, string? DeveloperName, string? Author, PluginSourceKind SourceKind,
    string PackageSha256, string DisclosureSha256, PluginTerms? Terms, PluginNetworkDisclosure? Network,
    IReadOnlyList<string> Permissions)
{
    public string Fingerprint => PackageSha256 + ":" + DisclosureSha256;
}
