namespace WindowsIsland.Contracts;

/// <summary>定义由宿主管理的插件生命周期。</summary>
/// <remarks>宿主依次初始化、启动和停止插件，最后释放插件；同一实例可以在停止后重新启动。</remarks>
public interface IIslandPlugin : IDisposable
{
    /// <summary>获取全局唯一且与清单一致的插件标识，格式为 wini_plugin_名称。</summary>
    string Id { get; }

    /// <summary>获取插件的显示名称。</summary>
    string DisplayName { get; }

    /// <summary>接收当前插件的宿主服务并准备内部状态，不开始监听或推送历史消息。</summary>
    /// <param name="context">绑定到当前插件标识的宿主上下文，在插件释放前保持有效。</param>
    /// <returns>初始化完成时结束的任务。</returns>
    Task InitializeAsync(IPluginContext context);

    /// <summary>启动插件拥有的后台监听并建立必要的会话基线，启动完成后返回。</summary>
    /// <remarks>持续监听任务由插件保存，重复启动不得创建重复订阅。</remarks>
    /// <returns>监听启动完成时结束的任务，而非整个监听循环。</returns>
    Task StartAsync();

    /// <summary>停止监听和通知推送，等待后台任务退出并释放本轮订阅；重复停止应安全。</summary>
    /// <returns>本轮监听完全停止时结束的任务。</returns>
    Task StopAsync();
}
