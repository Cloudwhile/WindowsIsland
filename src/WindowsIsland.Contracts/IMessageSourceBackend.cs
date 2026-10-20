namespace WindowsIsland.Contracts;

/// <summary>提供宿主拥有的既有来源服务适配能力，供内置插件经上下文取得。</summary>
/// <remarks>实现及平台资源留在宿主；此接口不构成外部来源发现或加载机制。</remarks>
public interface IMessageSourceBackend : IDisposable, IPluginStatusProvider, IPluginPolling, IPluginNotificationFilter
{
    /// <summary>在既有服务收到通知或补全元数据时触发。</summary>
    event Action<PluginNotification>? NotificationReceived;

    /// <summary>启动此轮服务和监听，建立静默基线。</summary>
    /// <returns>启动完成时结束的任务。</returns>
    Task StartAsync();
}

/// <summary>提供由宿主现有计时器驱动的可选轮询能力。</summary>
public interface IPluginPolling
{
    /// <summary>执行一次来源刷新，不创建独立的宿主计时器。</summary>
    /// <returns>本次轮询完成时结束的任务。</returns>
    Task PollAsync();
}

/// <summary>提供跨来源的会话通知过滤能力。</summary>
public interface IPluginNotificationFilter
{
    /// <summary>判断匹配此插件的通知是否满足会话策略，包括其他来源的同一会话。</summary>
    /// <param name="notification">待过滤的通知。</param>
    /// <returns>允许继续路由时为真。</returns>
    bool Allows(PluginNotification notification);
}

/// <summary>提供一次手动连接重试能力。</summary>
public interface IPluginRetry
{
    /// <summary>获取当前是否可以重试。</summary>
    bool CanRetry { get; }

    /// <summary>请求一次立即重试，不重放缺失期间的历史消息。</summary>
    void Retry();
}
