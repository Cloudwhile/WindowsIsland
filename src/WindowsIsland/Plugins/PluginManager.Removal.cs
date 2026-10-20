namespace WindowsIsland.Plugins;

internal sealed partial class PluginManager
{
    /// <summary>停止并移除非内置插件，持久保存删除结果，不删除共享程序集文件。</summary>
    /// <param name="id">待删除插件的稳定标识。</param>
    public async Task RemoveAsync(string id)
    {
        if (_disposed) return;
        var entry = _entries.FirstOrDefault(item => item.Registration.Definition.Id == id)
            ?? throw new ArgumentException("Unknown plugin identifier.", nameof(id));
        if (!entry.Registration.Definition.CanRemove) throw new InvalidOperationException("Built-in plugins cannot be removed.");
        if (entry.Removed) return;
        // 先持久化；保存失败时继续保留原实例和设置。
        _store.RemovePlugin(id);
        entry.Removed = true;
        Stop(entry);
        await _operations.WaitAsync();
        try { await entry.Cleanup; }
        finally { _operations.Release(); }
    }
}
