namespace WindowsIsland.Plugins;

internal static class PluginErrors
{
    public static string Key(Exception error) => error.Message switch
    {
        "只能安装 ZIP 插件包。" => "PluginErrorZip",
        "ZIP 根目录必须且只能包含一个 manifest.json。" => "PluginErrorManifest",
        "插件标识无效或与内置插件冲突。" => "PluginErrorId",
        "插件合约版本与消息岛不兼容。" => "PluginErrorContract",
        "找不到入口程序集。" or "找不到清单声明的入口类型。" or "插件入口必须是公开的插件实现类型。" or "插件入口缺少公开的无参数构造函数。" => "PluginErrorEntry",
        "不能安装比当前版本更旧的插件。" => "PluginErrorDowngrade",
        "插件版本必须符合 SemVer。" => "PluginErrorVersion",
        "插件路径无效。" or "插件路径超出目录。" or "插件目录不能包含符号链接或重解析点。" or "ZIP 含重复路径、特殊文件或符号链接。" => "PluginErrorPath",
        "插件已从其他来源安装，请先卸载。" => "PluginErrorSourceConflict",
        "插件启动失败，已恢复旧版本。" => "PluginErrorStart",
        "官方源地址无效。" => "PluginErrorSourceAddress",
        "插件 SHA256 校验失败。" => "PluginErrorHash",
        "插件包与官方索引不一致。" => "PluginErrorIndexMismatch",
        "插件已不在当前官方索引中。" => "PluginErrorNotListed",
        "插件开发者与已安装版本不一致。" => "PluginErrorIndexMismatch",
        _ => "PluginInstallFailed"
    };
}
