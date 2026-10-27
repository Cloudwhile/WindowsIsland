using System.Text.Json;
using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

// 此记录只由完成目录索引与包校验的宿主安装事务写入，不能从包内导入。
internal static class PluginConsentProvenance
{
    private sealed record Receipt(int Schema, string PluginId, string Generation, string PackageHash);

    internal static string? Read(string root, string id)
    {
        var path = PluginHostFiles.PathFor(root, "host-provenance", id);
        if (!File.Exists(path)) return null;
        using var file = PluginFileSafety.OpenSingleLinkRead(path);
        if (file.Length > 4096) throw new InvalidDataException("Invalid provenance receipt.");
        using var reader = new StreamReader(file);
        return reader.ReadToEnd();
    }

    internal static void Restore(string root, string id, string? receipt)
    {
        var path = PluginHostFiles.PathFor(root, "host-provenance", id);
        if (receipt is null) { if (File.Exists(path)) File.Delete(path); }
        else PluginHostFiles.WriteAtomic(path, receipt);
    }

    internal static void Record(ExternalPlugin plugin)
    {
        var generation = PluginConsentPolicy.ReadGeneration(plugin.HostRoot, plugin.Manifest.Id)
            ?? throw new InvalidDataException("Missing installation generation.");
        Restore(plugin.HostRoot, plugin.Manifest.Id, JsonSerializer.Serialize(new Receipt(1, plugin.Manifest.Id,
            generation, PluginConsentPolicy.HashPackage(plugin.Directory)), PluginManifestReader.Json));
    }

    internal static bool Verify(ExternalPlugin plugin, string? packageHash = null)
    {
        try
        {
            var text = Read(plugin.HostRoot, plugin.Manifest.Id);
            if (text is null) return false;
            var receipt = JsonSerializer.Deserialize<Receipt>(text, PluginManifestReader.Json);
            return receipt is { Schema: 1 } && receipt.PluginId == plugin.Manifest.Id
                && receipt.Generation == PluginConsentPolicy.ReadGeneration(plugin.HostRoot, plugin.Manifest.Id)
                && receipt.PackageHash == (packageHash ?? PluginConsentPolicy.HashPackage(plugin.Directory));
        }
        catch { return false; }
    }

    internal static void Require(ExternalPlugin plugin, string? packageHash = null)
    {
        if (plugin.Manifest.SourceKind == PluginSourceKind.Official && !Verify(plugin, packageHash))
            throw new PluginException(PluginErrorCode.IndexMismatch, "插件来源无法验证，请从默认插件源重新安装。");
    }
}
