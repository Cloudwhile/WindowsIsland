namespace WindowsIsland.Contracts;

/// <summary>描述系统通知授权结果。</summary>
public enum PluginNotificationAccess
{
    /// <summary>应用尚未完成系统通知初始化。</summary>
    NeedsRegistration,
    /// <summary>尚未授权。</summary>
    Waiting,
    /// <summary>已允许访问。</summary>
    Allowed,
    /// <summary>已拒绝访问。</summary>
    Denied,
    /// <summary>授权能力不可用。</summary>
    Unavailable
}

/// <summary>提供可选的系统通知授权能力。</summary>
public interface IPluginNotificationAccess
{
    /// <summary>获取当前授权结果。</summary>
    PluginNotificationAccess Access { get; }

    /// <summary>在授权结果变化时触发。</summary>
    event Action<PluginNotificationAccess>? AccessChanged;

    /// <summary>由宿主在界面线程发起系统通知授权。</summary>
    /// <returns>授权请求完成时结束的任务。</returns>
    Task RequestAccessAsync();
}
