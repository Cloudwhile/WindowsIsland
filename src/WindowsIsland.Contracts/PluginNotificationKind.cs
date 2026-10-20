namespace WindowsIsland.Contracts;

/// <summary>保留既有通知管线区分传递途径的语义。</summary>
public enum PluginNotificationKind
{
    /// <summary>系统通知，包括通过系统通知接入的其他应用。</summary>
    SystemNotification,
    /// <summary>客户端原生桥接或弹窗回退。</summary>
    ClientHook,
    /// <summary>系统电源事件。</summary>
    Power,
    /// <summary>客户端辅助访问会话监听。</summary>
    ClientAutomation
}
