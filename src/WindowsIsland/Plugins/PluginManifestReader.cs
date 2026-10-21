using System.Text.Json;
using System.Text.Encodings.Web;
using WindowsIsland.Contracts;
using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

internal static class PluginManifestReader
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static bool Reserved(string id) => id is BuiltInPluginIds.System or BuiltInPluginIds.WeChat or BuiltInPluginIds.Telegram or BuiltInPluginIds.Power;
    // 开发者标识采用与插件名称相同的小写字符和单下划线规则，不带插件类型后缀。
    public static bool DeveloperIdValid(string? id) => id is { Length: > 0 and <= 64 } && PluginId.IsValid("wini_" + id + "_plugin");
    public static bool GitHubRepository(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "github.com" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.AbsolutePath.Trim('/').Split('/') is [var owner, var repo]
        && owner.Length > 0 && repo.Length > 0;

    public static PluginManifest Read(string root, string? expectedId = null)
    {
        var path = PluginPaths.Relative(root, "manifest.json");
        if (new FileInfo(path).Length > 256 * 1024) throw new InvalidDataException("插件清单过大。");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("插件清单为空。");
        Validate(manifest);
        if (expectedId is not null && manifest.Id != expectedId) throw new InvalidDataException("目录名称与插件标识不一致。");
        if (!File.Exists(PluginPaths.Relative(root, manifest.EntryAssembly))) throw new InvalidDataException("找不到入口程序集。");
        return manifest;
    }

    public static void Validate(PluginManifest manifest)
    {
        if (!PluginId.IsValid(manifest.Id) || manifest.Id.Length > 80 || Reserved(manifest.Id)) throw new InvalidDataException("插件标识无效或与内置插件冲突。");
        if (!PluginContract.IsCompatible(manifest.ContractVersion)) throw new InvalidDataException("插件合约版本与消息岛不兼容。");
        if (!ReleaseVersion.TryParse(manifest.Version, out _)) throw new InvalidDataException("插件版本必须符合 SemVer。");
        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 200 || string.IsNullOrWhiteSpace(manifest.EntryType))
            throw new InvalidDataException("插件名称或入口类型无效。");
        if (manifest.DeveloperId != "" && !DeveloperIdValid(manifest.DeveloperId)) throw new InvalidDataException("开发者标识无效。");
        if (manifest.RepositoryUrl is not null && !GitHubRepository(manifest.RepositoryUrl)) throw new InvalidDataException("仓库必须是公开 GitHub 仓库地址。");
        if (manifest.Permissions is null || manifest.Permissions.Length > 64 || manifest.Permissions.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 80)
            || manifest.Permissions.Distinct(StringComparer.Ordinal).Count() != manifest.Permissions.Length)
            throw new InvalidDataException("插件权限声明无效。");
    }
}
