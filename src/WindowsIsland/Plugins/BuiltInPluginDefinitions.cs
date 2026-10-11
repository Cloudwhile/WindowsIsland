using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

internal static class BuiltInPluginDefinitions
{
    // Keep the existing persisted fields, localized titles and automation IDs.
    public static readonly NotificationPluginDefinition System = new("system", "SystemNotifications", "SystemNotificationsToggle", 0xE8F2,
        DiagnosticArea.Notifications, s => s.SystemNotifications, (s, value) => s with { SystemNotifications = value },
        n => n.Source == NotificationSource.SystemNotification);
    public static readonly NotificationPluginDefinition WeChat = new("wechat", "WeChat", "WeChatToggle", 0xE77B,
        DiagnosticArea.WeChat, s => s.WeChat, (s, value) => s with { WeChat = value },
        n => MessengerIdentity.FromNotification(n) == "wechat");
    public static readonly NotificationPluginDefinition Telegram = new("telegram", "Telegram", "TelegramToggle", 0xE724,
        DiagnosticArea.Telegram, s => s.Telegram, (s, value) => s with { Telegram = value },
        n => MessengerIdentity.FromNotification(n) == "telegram");
    public static readonly NotificationPluginDefinition Power = new("power", "Power", "PowerToggle", 0xE7E8,
        DiagnosticArea.Notifications, s => s.Power, (s, value) => s with { Power = value },
        n => n.Source == NotificationSource.Power);
}
