namespace WindowsIsland.Contracts;

/// <summary>控制宿主提供的微信监听；仅向经默认插件源核验的官方微信插件提供。</summary>
/// <remarks>消息由宿主直接投递，不向插件返回消息正文、进程句柄或任意自动化能力。</remarks>
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
