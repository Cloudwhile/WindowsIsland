namespace WindowsIsland.Plugins;

internal static class BuiltInPluginIds
{
    public const string System = "wini_plugin_system";
    public const string Telegram = "wini_plugin_telegram";
    public const string Power = "wini_plugin_power";
    public const string WeChat = "wini_plugin_wechat";
    // 已停用来源的标识仍禁止外部占用。
    public static bool IsReserved(string id) => id is System or Telegram or Power or WeChat or "wini_plugin_countdown";

    public static string FromLegacy(string id) => id switch
    {
        "system" or "wini_system_plugin" => System,
        "telegram" or "wini_telegram_plugin" => Telegram,
        "power" or "wini_power_plugin" => Power,
        "wechat" or "wini_wechat_plugin" => WeChat,
        _ => id
    };
}
