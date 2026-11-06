namespace WindowsIsland.Contracts;

/// <summary>可选的设置变更回调。宿主按插件串行调度，停止后不再调用。</summary>
public interface IPluginSettingsChanged
{
    /// <summary>设置已成功持久化；仅传入变化的键，不传敏感值。重新读取 context.Settings 获取当前值。</summary>
    /// <param name="keys">本插件范围内已变化的键；连续变更可能合并。</param>
    /// <returns>变更处理任务，不应等待永久后台循环。</returns>
    Task OnSettingsChangedAsync(IReadOnlyList<string> keys);
}
