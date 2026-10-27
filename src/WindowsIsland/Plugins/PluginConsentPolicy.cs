using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

internal static class PluginConsentPolicy
{
    private sealed record Receipt(int Schema, string PluginId, string Fingerprint, DateTimeOffset AcceptedAt);
    private const int MaximumFiles = 4096;
    private const long MaximumBytes = 256L * 1024 * 1024;

    public static PluginConsentDescriptor Describe(ExternalPlugin external)
    {
        PluginPaths.CheckTree(external.Directory);
        var manifest = PluginManifestReader.Read(external.Directory, external.Manifest.Id) with { SourceKind = external.Manifest.SourceKind };
        var expected = JsonSerializer.Serialize(external.Manifest, PluginManifestReader.Json);
        if (JsonSerializer.Serialize(manifest, PluginManifestReader.Json) != expected)
            throw new PluginException(PluginErrorCode.Permissions, "插件清单已改变，请重新安装。");
        if (manifest.Permissions.Contains(PluginPermissions.Network, StringComparer.Ordinal) && manifest.Network is null)
            throw new PluginException(PluginErrorCode.Permissions, "缺少联网须知，请联系开发者或从默认插件源重新安装。");
        var disclosure = JsonSerializer.Serialize(new
        {
            manifest.Id, manifest.Name, manifest.Version, manifest.Author, manifest.DeveloperId, manifest.DeveloperName,
            manifest.SourceKind, Permissions = manifest.Permissions.Order(StringComparer.Ordinal).ToArray(), manifest.Terms, manifest.Network
        }, PluginManifestReader.Json);
        var packageHash = HashPackage(external.Directory);
        PluginConsentProvenance.Require(external, packageHash);
        return new(manifest.Id, manifest.Name, manifest.Version, manifest.DeveloperId, manifest.DeveloperName, manifest.Author,
            manifest.SourceKind, packageHash, Hash(Encoding.UTF8.GetBytes(disclosure)), manifest.Terms, manifest.Network,
            Array.AsReadOnly(manifest.Permissions.ToArray()), ReadGeneration(external.HostRoot, manifest.Id) ?? "");
    }

    public static bool HasConsent(ExternalPlugin external) => HasConsent(external, null);

    // 仅供已锁定整个安装树的运行会话使用。
    internal static bool HasConsent(ExternalPlugin external, PluginConsentDescriptor? locked)
    {
        try
        {
            Receipt? receipt;
            using (var stream = PluginFileSafety.OpenSingleLinkRead(ReceiptPath(external)))
            {
                if (stream.Length > 8192) return false;
                receipt = JsonSerializer.Deserialize<Receipt>(stream, PluginManifestReader.Json);
            }
            if (receipt is not { Schema: 1 } || receipt.PluginId != external.Manifest.Id
                || receipt.AcceptedAt <= DateTimeOffset.UnixEpoch || receipt.AcceptedAt > DateTimeOffset.UtcNow.AddMinutes(5)) return false;
            if (locked is null) return receipt.Fingerprint == Describe(external).Fingerprint;
            PluginConsentProvenance.Require(external, locked.PackageSha256);
            return locked.InstallGeneration == (ReadGeneration(external.HostRoot, external.Manifest.Id) ?? "")
                && receipt.Fingerprint == locked.Fingerprint;
        }
        catch { return false; }
    }

    public static void Grant(ExternalPlugin external, PluginConsentDescriptor approved)
    {
        var current = Describe(external);
        if (current.PluginId != approved.PluginId || current.Fingerprint != approved.Fingerprint)
            throw new PluginException(PluginErrorCode.Permissions, "确认期间插件包或条款已改变。");
        PluginHostFiles.WriteAtomic(ReceiptPath(external), JsonSerializer.Serialize(
            new Receipt(1, current.PluginId, current.Fingerprint, DateTimeOffset.UtcNow), PluginManifestReader.Json));
    }

    public static void Revoke(ExternalPlugin external)
    {
        var path = ReceiptPath(external);
        if (File.Exists(path)) File.Delete(path);
    }

    public static string HostSettingsPath(ExternalPlugin external)
    {
        var path = PluginHostFiles.PathFor(external.HostRoot, "host-settings", external.Manifest.Id);
        PluginPaths.CheckLinks(path + ".tmp");
        if (File.Exists(path + ".tmp")) PluginFileSafety.CheckSingleLink(path + ".tmp");
        return path;
    }
    internal static string? ReadGeneration(string root, string id)
    {
        var path = PluginHostFiles.PathFor(root, "host-installations", id);
        if (!File.Exists(path)) return null;
        using var stream = PluginFileSafety.OpenSingleLinkRead(path);
        if (stream.Length > 128) throw new InvalidDataException("Invalid installation generation.");
        using var reader = new StreamReader(stream);
        var value = reader.ReadToEnd();
        if (!Guid.TryParseExact(value, "N", out _)) throw new InvalidDataException("Invalid installation generation.");
        return value;
    }

    internal static void WriteGeneration(string root, string id, string? generation)
    {
        var path = PluginHostFiles.PathFor(root, "host-installations", id);
        if (generation is null) { if (File.Exists(path)) File.Delete(path); }
        else PluginHostFiles.WriteAtomic(path, generation);
    }

    private static string ReceiptPath(ExternalPlugin external) => PluginHostFiles.PathFor(external.HostRoot, "host-consents", external.Manifest.Id);

    internal static string HashPackage(string directory)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(directory, path), StringComparer.Ordinal).ToArray();
        if (files.Length is 0 or > MaximumFiles) throw new PluginException(PluginErrorCode.ExpandedSize, "插件包文件数量超出限制。");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        foreach (var path in files)
        {
            using var input = PluginFileSafety.OpenSingleLinkRead(path);
            total = checked(total + input.Length);
            if (total > MaximumBytes) throw new PluginException(PluginErrorCode.ExpandedSize, "插件包大小超出限制。");
            var name = Encoding.UTF8.GetBytes(Path.GetRelativePath(directory, path).Replace('\\', '/'));
            hash.AppendData(BitConverter.GetBytes(name.Length));
            hash.AppendData(name);
            hash.AppendData(SHA256.HashData(input));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
