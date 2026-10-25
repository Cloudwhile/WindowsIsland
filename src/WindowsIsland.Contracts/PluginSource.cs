using System.Text.Json.Serialization;

namespace WindowsIsland.Contracts;

/// <summary>描述官方目录中按开发者区分的已收录来源。</summary>
/// <param name="DeveloperId">严格的小写开发者标识。</param>
/// <param name="DeveloperName">开发者显示名称。</param>
/// <param name="RepositoryUrl">公开的 GitHub 仓库地址。</param>
/// <param name="IndexUrl">该开发者已收录插件列表的绝对地址。</param>
/// <param name="LastSync">宿主保存的最近成功同步时间。</param>
public sealed record PluginSource(
    [property: JsonPropertyName("developerId")] string DeveloperId,
    [property: JsonPropertyName("developerName")] string DeveloperName,
    [property: JsonPropertyName("repositoryUrl")] string RepositoryUrl,
    [property: JsonPropertyName("pluginsUrl")] string IndexUrl,
    [property: JsonPropertyName("lastSync"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LastSync = null);

/// <summary>描述官方目录中的一个已收录插件版本，不包含审核过程状态。</summary>
/// <param name="Id">插件标识。</param>
/// <param name="Name">显示名称。</param>
/// <param name="Version">完整语义版本。</param>
/// <param name="DownloadUrl">公开 GitHub 仓库的 ZIP 发布附件地址。</param>
/// <param name="Sha256">附件的 SHA-256 十六进制摘要。</param>
/// <param name="MinHostVersion">最低宿主语义版本。</param>
/// <param name="ContractVersion">合约大版本。</param>
/// <param name="Permissions">版本声明的宿主能力。</param>
/// <param name="Description">插件说明。</param>
/// <param name="RepositoryUrl">公开的 GitHub 仓库地址。</param>
/// <param name="DeveloperId">所属开发者标识。</param>
/// <param name="Network">默认插件源提供的联网须知，作为包内声明缺失时的补充。</param>
public sealed record PluginCatalogEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("downloadUrl")] string DownloadUrl,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("minHostVersion")] string MinHostVersion,
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("permissions")] string[] Permissions,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("repositoryUrl")] string RepositoryUrl,
    [property: JsonPropertyName("developerId")] string DeveloperId,
    [property: JsonPropertyName("network")] PluginNetworkDisclosure? Network = null);
