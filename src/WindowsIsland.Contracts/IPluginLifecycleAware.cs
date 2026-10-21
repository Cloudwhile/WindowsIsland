namespace WindowsIsland.Contracts;

/// <summary>插件可选实现的停止及卸载钩子；钩子异常仅记录诊断，宿主继续清理。</summary>
public interface IPluginLifecycleAware
{
    /// <summary>在 StopAsync 前通知停止原因。</summary>
    /// <param name="reason">宿主发起此次停止的原因。</param>
    Task OnStoppingAsync(PluginStopReason reason);

    /// <summary>删除代码目录前调用；停用的插件也会收到此钩子，不会因此启动监听。</summary>
    Task OnUninstallAsync() => Task.CompletedTask;
}

/// <summary>宿主停止插件的原因。</summary>
public enum PluginStopReason
{
    /// <summary>用户停用插件。</summary>
    Disabled,
    /// <summary>用户卸载插件。</summary>
    Removed,
    /// <summary>以另一个版本替换插件。</summary>
    Updating,
    /// <summary>宿主退出。</summary>
    Shutdown,
    /// <summary>插件异常，宿主隔离并停止该实例。</summary>
    Failed
}
