using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

internal sealed partial class PluginManager
{
    /// <summary>停止并移除插件；外部代码目录删除后保留独立数据。</summary>
    /// <param name="id">待删除插件的稳定标识。</param>
    public async Task RemoveAsync(string id)
    {
        await _mutations.WaitAsync();
        try { await RemoveCoreAsync(id); }
        finally { _mutations.Release(); }
    }

    private async Task RemoveCoreAsync(string id)
    {
        if (_disposed) return;
        var entry = Entries.FirstOrDefault(item => item.Registration.Definition.Id == id)
            ?? throw new ArgumentException("Unknown plugin identifier.", nameof(id));
        if (!entry.Registration.Definition.CanRemove) throw new InvalidOperationException("Built-in plugins cannot be removed.");
        if (entry.Removed) return;
        if (entry.Registration.Definition.Metadata?.External is { } external)
        {
            await RemoveExternalAsync(entry, external);
            return;
        }
        // 先持久化；保存失败时继续保留原实例和设置。
        _store.RemovePlugin(id);
        entry.Removed = true;
        await PrepareLifecycleAsync(entry);
        Stop(entry, PluginStopReason.Removed);
        await _operations.WaitAsync();
        try { await entry.Cleanup; }
        finally { _operations.Release(); }
    }

    private async Task RemoveExternalAsync(Entry entry, ExternalPlugin external)
    {
        await _operations.WaitAsync();
        var wasRemoved = _store.IsPluginRemoved(external.Manifest.Id);
        Exception? failure = null;
        try
        {
            if (entry.Removed) return;
            _store.MarkExternalRemoved(external.Manifest.Id, true);
            entry.Removed = true;
            await PrepareLifecycleAsync(entry);
            Stop(entry, PluginStopReason.Removed);
            await entry.Cleanup;
            entry.Cleanup = Task.CompletedTask;
            await external.VerifyUnloadAsync();
            await Task.Run(() => PluginFiles.RemoveCode(external.Directory));
        }
        catch (Exception error)
        {
            entry.Removed = false;
            _store.MarkExternalRemoved(external.Manifest.Id, wasRemoved);
            entry.Failed = false;
            failure = error;
        }
        finally { _operations.Release(); }
        if (failure is not null)
        {
            if (_started) await ReconcileAsync();
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
