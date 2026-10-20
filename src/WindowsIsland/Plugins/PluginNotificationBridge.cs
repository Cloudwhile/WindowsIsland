using WindowsIsland.Contracts;
using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

internal static class PluginNotificationBridge
{
    public static PluginNotification ToContract(string sourceId, IslandNotification notification) => new(sourceId,
        notification.Title, null, notification.Body, notification.CreatedAt, notification.AppIcon, notification.SenderAvatar,
        notification.EventId, notification.OriginProcessId, notification.AppName, notification.AppId, notification.Id,
        notification.Symbol, notification.IsMetadataUpdate, notification.Source switch
        {
            NotificationSource.SystemNotification => PluginNotificationKind.SystemNotification,
            NotificationSource.ClientHook => PluginNotificationKind.ClientHook,
            NotificationSource.Power => PluginNotificationKind.Power,
            NotificationSource.ClientAutomation => PluginNotificationKind.ClientAutomation,
            _ => throw new ArgumentOutOfRangeException(nameof(notification))
        });

    public static IslandNotification ToHost(PluginNotification notification) => new(notification.Id, notification.CreatedAt,
        notification.AppName ?? notification.SourceId, notification.ConversationName, notification.Body, notification.AppIcon,
        notification.Kind switch
        {
            PluginNotificationKind.SystemNotification => NotificationSource.SystemNotification,
            PluginNotificationKind.ClientHook => NotificationSource.ClientHook,
            PluginNotificationKind.Power => NotificationSource.Power,
            PluginNotificationKind.ClientAutomation => NotificationSource.ClientAutomation,
            _ => throw new ArgumentOutOfRangeException(nameof(notification))
        }, notification.AppId, notification.DeduplicationKey, notification.Symbol, notification.SenderAvatar,
        notification.OriginProcessId, notification.IsMetadataUpdate);

    public static PluginStatus FromKey(string key) => key switch
    {
        "Connected" or "On" => new(PluginConnectionState.Connected),
        "NotRunning" or "Off" => new(PluginConnectionState.NotRunning),
        "TelegramNotDetected" => new(PluginConnectionState.NotRunning, PluginStatusReason.ClientNotDetected),
        "WeChatUnsupported" => new(PluginConnectionState.Disconnected, PluginStatusReason.VersionNotSupported),
        "WeChatConnectFailed" => new(PluginConnectionState.Disconnected, PluginStatusReason.ConnectionFailed),
        "Unavailable" => new(PluginConnectionState.Disconnected, PluginStatusReason.Unavailable),
        _ => new(PluginConnectionState.Disconnected)
    };

    public static string StatusKey(string id, PluginStatus status) => status.State switch
    {
        PluginConnectionState.Connected => id is BuiltInPluginIds.Power or BuiltInPluginIds.System ? "On" : "Connected",
        PluginConnectionState.NotRunning => status.Reason == PluginStatusReason.ClientNotDetected ? "TelegramNotDetected" : "NotRunning",
        _ => status.Reason switch
        {
            PluginStatusReason.VersionNotSupported => "WeChatUnsupported",
            PluginStatusReason.ConnectionFailed when id == BuiltInPluginIds.WeChat => "WeChatConnectFailed",
            PluginStatusReason.PermissionDenied => "Denied",
            PluginStatusReason.Unavailable or PluginStatusReason.ConnectionFailed => "Unavailable",
            _ => "NotConnected"
        }
    };
}
