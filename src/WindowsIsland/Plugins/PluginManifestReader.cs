using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text;
using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

internal static class PluginManifestReader
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static bool Reserved(string id) => BuiltInPluginIds.IsReserved(id);
    // C13：开发者标识与目录使用同一字符集，不带插件前缀。
    public static bool DeveloperIdValid(string? id) => id is { Length: > 0 and <= 64 } && PluginId.IsValid(PluginId.Prefix + id);
    public static bool GitHubRepository(string? value) => PluginCatalogPolicy.Repository(value);

    public static PluginManifest Read(string root, string? expectedId = null)
    {
        var path = PluginPaths.Relative(root, "manifest.json");
        using var input = PluginFileSafety.OpenSingleLinkRead(path);
        if (input.Length > 256 * 1024) throw new PluginException(PluginErrorCode.ManifestSize, "插件清单过大。");
        using var bytes = new MemoryStream();
        input.CopyTo(bytes);
        var text = new UTF8Encoding(false, true).GetString(bytes.ToArray()).TrimStart('\uFEFF');
        var manifest = JsonSerializer.Deserialize<PluginManifest>(text, Json)
            ?? throw new PluginException(PluginErrorCode.ManifestEmpty, "插件清单为空。");
        Validate(manifest);
        if (expectedId is not null && manifest.Id != expectedId) throw new PluginException(PluginErrorCode.DirectoryId, "目录名称与插件标识不一致。");
        if (!File.Exists(PluginPaths.Relative(root, manifest.EntryAssembly))) throw new PluginException(PluginErrorCode.Entry, "找不到入口程序集。");
        _ = PluginStaticSettings.Read(root);
        return manifest;
    }

    public static void Validate(PluginManifest manifest)
    {
        if (manifest.Description?.Length > 8000 || manifest.Author?.Length > 200)
            throw new PluginException(PluginErrorCode.DescriptionSize, "插件说明或作者名称过长。");
        if (!PluginId.IsValid(manifest.Id) || manifest.Id.Length > 80 || Reserved(manifest.Id)) throw new PluginException(PluginErrorCode.Id, "插件标识无效或与内置插件冲突。");
        if (!PluginContract.IsCompatible(manifest.ContractVersion)) throw new PluginException(PluginErrorCode.Contract, "插件合约版本与消息岛不兼容。");
        if (!CatalogVersion.TryParse(manifest.Version, out _)) throw new PluginException(PluginErrorCode.Version, "插件版本必须符合 SemVer。");
        if (manifest.MinimumHostVersion is not null && !CatalogVersion.TryParse(manifest.MinimumHostVersion, out _))
            throw new PluginException(PluginErrorCode.HostVersion, "manifest.json $.minimumHostVersion: 使用 SemVer，例如 0.1.0。");
        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 200 || string.IsNullOrWhiteSpace(manifest.EntryType))
            throw new PluginException(PluginErrorCode.EntryName, "插件名称或入口类型无效。");
        if (manifest.DeveloperId != "" && !DeveloperIdValid(manifest.DeveloperId)) throw new PluginException(PluginErrorCode.DeveloperId, "开发者标识无效。");
        if (manifest.RepositoryUrl is not null && !GitHubRepository(manifest.RepositoryUrl)) throw new PluginException(PluginErrorCode.Repository, "仓库必须是公开 GitHub 仓库地址。");
        if (manifest.Permissions is null || manifest.Permissions.Length > 64 || manifest.Permissions.Any(p => !PluginCatalogPolicy.Identifier(p, 80))
            || manifest.Permissions.Distinct(StringComparer.Ordinal).Count() != manifest.Permissions.Length)
            throw new PluginException(PluginErrorCode.Permissions, "插件权限声明无效。");
        PluginDesktopPolicy.Validate(manifest);
        ValidateSecurityMetadata(manifest);
    }

    internal static void ValidateForHost(PluginManifest manifest, string hostVersion)
    {
        if (!CatalogVersion.TryParse(hostVersion.StartsWith('v') ? hostVersion[1..] : hostVersion, out var host))
            throw new PluginException(PluginErrorCode.HostVersion, "目标宿主版本必须符合 SemVer，例如 0.1.0。");
        if (manifest.MinimumHostVersion is { } minimum && (!CatalogVersion.TryParse(minimum, out var required) || host.CompareTo(required) < 0))
            throw new PluginException(PluginErrorCode.HostVersion, "目标宿主低于 manifest.json $.minimumHostVersion；升级宿主或选择兼容的插件版本。");
    }

    internal static void ValidateSecurityMetadata(PluginManifest manifest)
    {
        if (manifest.Terms is { } terms)
        {
            if (!Text(terms.Revision, 128) || !Text(terms.Text, 65536)) Invalid();
            try { if (new UTF8Encoding(false, true).GetByteCount(terms.Text) > 65536) Invalid(); }
            catch (EncoderFallbackException) { Invalid(); }
        }
        if (manifest.Network is not null && !manifest.Permissions.Contains(PluginPermissions.Network, StringComparer.Ordinal)) Invalid();
        if (manifest.Network is not { } network) return;
        if (network.Origins is not { Length: > 0 and <= 16 } || network.DataCategories is not { Length: > 0 and <= 16 }
            || !Text(network.Purpose, PluginNetworkLimits.MaximumPurposeLength)
            || network.Notice is not null && !Text(network.Notice, 65536)
            || network.DataCategories.Any(value => !Text(value, 128))
            || network.DataCategories.Distinct(StringComparer.Ordinal).Count() != network.DataCategories.Length) Invalid();
        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in network.Origins)
        {
            if (value is null || value.Length > 2048 || value != value.Trim()
                || value.Any(char.IsControl) || value.IndexOfAny(['?', '#', '\\', '@']) >= 0
                || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
                || uri.AbsolutePath != "/" || uri.Host.Length == 0
                || !origins.Add(uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped))) Invalid();
        }
        static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum
            && value.All(c => !char.IsControl(c) || c is '\n' or '\r' or '\t');
        static void Invalid() => throw new PluginException(PluginErrorCode.Permissions, "插件条款或网络披露无效。");
    }
}
