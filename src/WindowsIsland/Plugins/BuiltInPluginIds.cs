namespace WindowsIsland.Plugins;

internal static class BuiltInPluginIds
{
    public const string System = "wini_system_plugin";
    public const string Telegram = "wini_telegram_plugin";
    public const string Power = "wini_power_plugin";
    public const string WeChat = "wini_wechat_plugin";

    public static string FromLegacy(string id) => id switch
    {
        "system" => System,
        "telegram" => Telegram,
        "power" => Power,
        "wechat" => WeChat,
        _ => id
    };
}
