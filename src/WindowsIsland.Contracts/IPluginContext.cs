namespace WindowsIsland.Contracts;

/// <summary>提供与当前插件绑定的宿主服务，不暴露界面对象。</summary>
public interface IPluginContext
{
    /// <summary>获取此上下文绑定的插件标识，与插件及清单中的标识一致。</summary>
    string PluginId { get; }

    /// <summary>获取已按当前插件标识隔离的设置存储，可从后台线程调用。</summary>
    IPluginSettings Settings { get; }

    /// <summary>获取当前插件的本机诊断入口，可从后台线程调用。</summary>
    IPluginDiagnostics Diagnostics { get; }

    /// <summary>获取宿主为当前插件提供的可选服务能力，不返回界面对象。</summary>
    /// <typeparam name="T">服务的合约接口类型。</typeparam>
    /// <returns>绑定到当前插件的服务，未提供该能力时为空引用。</returns>
    T? GetService<T>() where T : class => null;

    /// <summary>从任意线程提交通知，由宿主入队并调度到界面线程。</summary>
    /// <remarks>此调用不等待渲染；宿主校对来源标识，停止或释放后的延迟通知由宿主丢弃。</remarks>
    /// <param name="notification">来源标识与当前插件标识一致的通知数据。</param>
    void PublishNotification(PluginNotification notification);
}
