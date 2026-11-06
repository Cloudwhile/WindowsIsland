using WindowsIsland.Plugins;

namespace WindowsIsland.Plugin.Cli;

internal static class CliDiagnostic
{
    public static string Format(PluginException error)
    {
        var (location, correction) = error.Code switch
        {
            PluginErrorCode.Id or PluginErrorCode.IdConflict or PluginErrorCode.DirectoryId =>
                ("manifest.json $.id", "使用未被占用的 wini_plugin_ 标识，例如 wini_plugin_hello。"),
            PluginErrorCode.Contract => ("manifest.json $.contractVersion", "使用目标宿主支持的合约大版本，例如 1。"),
            PluginErrorCode.Version => ("manifest.json $.version", "使用 SemVer 字符串，例如 \"1.0.0\"。"),
            PluginErrorCode.HostVersion => ("manifest.json $.minimumHostVersion / --host-version", "使用兼容的 SemVer 目标版本，例如 doctor . --host-version 0.1.0。"),
            PluginErrorCode.Entry or PluginErrorCode.EntryName or PluginErrorCode.ManagedAssembly or PluginErrorCode.RuntimeEntry =>
                ("manifest.json $.entryAssembly / $.entryType", "先构建 DLL，填写相对路径和公开入口全名，例如 Hello.dll 与 Hello.Plugin；入口需实现 IIslandPlugin 并提供公开无参构造函数。"),
            PluginErrorCode.DeveloperId => ("manifest.json $.developerId", "使用 1–64 个字母、数字或下划线，例如 example_author。"),
            PluginErrorCode.Repository => ("manifest.json $.repositoryUrl", "使用 https://github.com/owner/repository，不附带查询参数或额外路径。"),
            PluginErrorCode.Permissions => ("manifest.json $.permissions / $.terms / $.network", "检查权限及披露字段；最小权限示例为 [\"notifications\"]，联网时补齐 origins、purpose 和 dataCategories。"),
            PluginErrorCode.DescriptionSize => ("manifest.json $.description / $.author", "description 最多 8000 字符，author 最多 200 字符。"),
            PluginErrorCode.HostDependency or PluginErrorCode.RuntimeHostDependency =>
                ("package dependencies", "仅引用独立 Contracts；移除宿主、WinUI 等程序集依赖。"),
            PluginErrorCode.MissingDependency => ("package dependencies", "包含构建输出的依赖 DLL，例如启用 CopyLocalLockFileAssemblies 后重新 pack。"),
            PluginErrorCode.Path or PluginErrorCode.PathId => ("package paths", "使用包内相对路径，例如 lib/Helper.dll；不要使用链接目录。"),
            PluginErrorCode.Manifest => ("manifest.json / settings.schema.json $", "检查对象结构；静态设置最小示例为 {\"schemaVersion\":1,\"fields\":[]}。"),
            _ => ("manifest.json / package", "对照清单和打包文档修复后，重新执行 wini-plugin validate 与 pack。")
        };
        return $"{location} [{error.Code}]: {error.Message} {correction}";
    }
}
