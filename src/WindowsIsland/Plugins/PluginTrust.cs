using System.Text.Json;
using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

internal sealed record PluginTrust(string[] Granted, string[] Denied) : IPluginPermissions
{
    public bool IsGranted(string permission) => Granted.Contains(permission, StringComparer.Ordinal);
    public static PluginTrust Create(PluginManifest manifest, IEnumerable<string>? approved = null)
    {
        var confirmations = (approved ?? []).ToHashSet(StringComparer.Ordinal);
        var granted = manifest.Permissions.Where(p => PluginPermissions.IsKnown(p) && (!PluginPermissions.IsHighRisk(p)
            || manifest.SourceKind == PluginSourceKind.Official && confirmations.Contains(p))).ToArray();
        return new(granted, manifest.Permissions.Except(granted, StringComparer.Ordinal).ToArray());
    }

    public static PluginTrust Read(ExternalPlugin external)
    {
        if (external.Manifest.SourceKind == PluginSourceKind.Manual) return Create(external.Manifest);
        try
        {
            // 授权记录由宿主写入独立数据目录，包中的同名文件不能授予能力。
            var path = Path.Combine(external.DataDirectory, "host-permissions.json");
            PluginPaths.CheckLinks(path);
            var grant = JsonSerializer.Deserialize<PermissionReceipt>(File.ReadAllText(path), PluginManifestReader.Json);
            return Create(external.Manifest, grant is not null && grant.Version == external.Manifest.Version
                && grant.DeveloperId == external.Manifest.DeveloperId ? grant.Granted : []);
        }
        catch { return Create(external.Manifest); }
    }
}

internal sealed record PermissionReceipt(string Version, string DeveloperId, string[] Granted);
