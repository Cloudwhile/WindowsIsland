namespace WindowsIsland.Contracts;

/// <summary>描述可由 System.Text.Json 读写的插件清单，不负责加载程序集或授予权限。</summary>
/// <param name="Id">插件全局唯一标识，格式为 wini_plugin_名称。</param>
/// <param name="Name">插件显示名称。</param>
/// <param name="Version">插件自身的版本字符串。</param>
/// <param name="EntryAssembly">相对于插件目录的入口程序集文件名。</param>
/// <param name="EntryType">实现插件生命周期接口的入口类型完整名称。</param>
/// <param name="ContractVersion">插件要求的合约大版本。</param>
/// <param name="Permissions">声明的权限标识数组，不需要权限时使用空数组。</param>
/// <param name="Author">可选的作者名称，最多 200 字符。</param>
/// <param name="Description">可选的插件说明，最多 8000 字符。</param>
/// <param name="DeveloperId">开发者标识，默认为空。</param>
/// <param name="DeveloperName">可选的开发者名称。</param>
/// <param name="RepositoryUrl">可选的公开 GitHub 仓库地址。</param>
/// <param name="SourceKind">来源类型，默认为手动安装。</param>
/// <param name="Terms">可选的包内纯文本条款及修订标识。</param>
/// <param name="Network">声明网络能力时必需的目的地、用途和数据类别。</param>
public sealed record PluginManifest(string Id, string Name, string Version, string EntryAssembly, string EntryType,
    int ContractVersion, string[] Permissions, string? Author = null, string? Description = null,
    string DeveloperId = "", string? DeveloperName = null, string? RepositoryUrl = null,
    PluginSourceKind SourceKind = PluginSourceKind.Manual, PluginTerms? Terms = null, PluginNetworkDisclosure? Network = null)
{
    /// <summary>可选的最低宿主 SemVer 版本；省略时仅检查合约大版本。</summary>
    public string? MinimumHostVersion { get; init; }
}
