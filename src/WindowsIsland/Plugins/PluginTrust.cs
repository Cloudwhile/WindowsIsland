using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

internal sealed record PluginTrust(string[] Granted, string[] Denied) : IPluginPermissions
{
    public bool IsGranted(string permission) => Granted.Contains(permission, StringComparer.Ordinal);
    public static PluginTrust Create(PluginManifest manifest, IEnumerable<string>? approved = null)
    {
        var granted = manifest.Permissions.Where(permission => permission is PluginPermissions.Notifications or PluginPermissions.Settings
            || permission == PluginPermissions.WeChat && WeChatHostBroker.IsOfficial(manifest)
            || permission == PluginPermissions.Network && manifest.Network is not null).ToArray();
        return new(granted, manifest.Permissions.Except(granted, StringComparer.Ordinal).ToArray());
    }

    // 不再信任插件可写数据目录中的旧 host-permissions.json；官方外部插件同样处理。
    public static PluginTrust Read(ExternalPlugin external) => Create(external.Manifest);
}
