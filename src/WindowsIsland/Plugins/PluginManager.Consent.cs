namespace WindowsIsland.Plugins;

internal sealed partial class PluginManager
{
    // 不执行插件代码来恢复安全状态；旧版保存的开启偏好不能替代本次授权。
    private void DisableUnapproved(Entry entry)
    {
        Stop(entry);
        entry.Failed = false;
        if (!entry.Registration.Definition.IsEnabled(_store.Current)) return;
        var disabled = entry.Registration.Definition.SetEnabled(_store.Current, false);
        _store.Save(disabled);
        _settings = _store.Current;
    }
}
