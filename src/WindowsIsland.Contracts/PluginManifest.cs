namespace WindowsIsland.Contracts;

/// <summary>描述可由 System.Text.Json 读写的插件清单，不负责加载程序集或授予权限。</summary>
/// <param name="Id">插件全局唯一标识。</param>
/// <param name="Name">插件显示名称。</param>
/// <param name="Version">插件自身的版本字符串。</param>
/// <param name="EntryAssembly">相对于插件目录的入口程序集文件名。</param>
/// <param name="EntryType">实现插件生命周期接口的入口类型完整名称。</param>
/// <param name="ContractVersion">插件要求的合约大版本。</param>
/// <param name="Permissions">声明的权限标识数组，不需要权限时使用空数组。</param>
/// <param name="Author">可选的作者名称。</param>
/// <param name="Description">可选的插件说明。</param>
public sealed record PluginManifest(string Id, string Name, string Version, string EntryAssembly, string EntryType,
    int ContractVersion, string[] Permissions, string? Author = null, string? Description = null);
