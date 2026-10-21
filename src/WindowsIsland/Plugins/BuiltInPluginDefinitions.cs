using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

internal static class BuiltInPluginDefinitions
{
    // Keep the existing persisted fields, localized titles and automation IDs.
    public static readonly NotificationPluginDefinition System = new(BuiltInPluginIds.System, "SystemNotifications", "SystemNotificationsToggle", 0xE8F2,
        DiagnosticArea.Notifications, s => s.SystemNotifications, (s, value) => s with { SystemNotifications = value },
        n => n.Source == NotificationSource.SystemNotification, PluginMetadata.BuiltIn);
    public static readonly NotificationPluginDefinition WeChat = new(BuiltInPluginIds.WeChat, "WeChat", "WeChatToggle", 0xE77B,
        DiagnosticArea.WeChat, s => s.WeChat, (s, value) => s with { WeChat = value },
        n => MessengerIdentity.FromNotification(n) == "wechat", PluginMetadata.BuiltIn);
    public static readonly NotificationPluginDefinition Telegram = new(BuiltInPluginIds.Telegram, "Telegram", "TelegramToggle", 0xE724,
        DiagnosticArea.Telegram, s => s.Telegram, (s, value) => s with { Telegram = value },
        n => MessengerIdentity.FromNotification(n) == "telegram", PluginMetadata.BuiltIn);
    public static readonly NotificationPluginDefinition Power = new(BuiltInPluginIds.Power, "Power", "PowerToggle", 0xE7E8,
        DiagnosticArea.Notifications, s => s.Power, (s, value) => s with { Power = value },
        n => n.Source == NotificationSource.Power, PluginMetadata.BuiltIn);
}
