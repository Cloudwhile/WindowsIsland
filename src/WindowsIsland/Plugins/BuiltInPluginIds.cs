namespace WindowsIsland.Plugins;

internal static class BuiltInPluginIds
{
    public const string System = "wini_plugin_system";
    public const string Telegram = "wini_plugin_telegram";
    public const string Power = "wini_plugin_power";
    public const string WeChat = "wini_plugin_wechat";
    // 已停用来源的标识仍禁止外部占用。
    public static bool IsReserved(string id) => id is System or Power or "wini_plugin_countdown";
}
