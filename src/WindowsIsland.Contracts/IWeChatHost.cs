namespace WindowsIsland.Contracts;

/// <summary>保留旧版二进制兼容性的历史接口；当前宿主不再提供此服务。</summary>
/// <remarks>微信业务由独立插件实现，桌面访问改用可选的 IPluginDesktop 服务。</remarks>
public interface IWeChatHost
{
    /// <summary>启动监听并建立静默基线，重复调用不重建监听。</summary>
    Task<WeChatHostStatus> StartAsync();
    /// <summary>刷新连接状态和弹窗监听。</summary>
    Task<WeChatHostStatus> PollAsync();
    /// <summary>请求一次连接重试。</summary>
    Task<WeChatHostStatus> RetryAsync();
}

/// <summary>描述微信监听的连接结果和可重试状态。</summary>
/// <param name="Status">连接结果。</param>
/// <param name="CanRetry">当前是否允许重试。</param>
public sealed record WeChatHostStatus(PluginStatus Status, bool CanRetry);
