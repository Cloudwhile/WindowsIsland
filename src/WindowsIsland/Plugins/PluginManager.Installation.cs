using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

internal sealed partial class PluginManager
{
    public async Task InstallAsync(PreparedPlugin prepared, bool overwrite = false, CancellationToken token = default)
    {
        await _mutations.WaitAsync(token);
        try { await InstallCoreAsync(prepared, overwrite, token); }
        finally { _mutations.Release(); }
    }

    private async Task InstallCoreAsync(PreparedPlugin prepared, bool overwrite, CancellationToken token)
    {
        if (_paths is null || _loader is null || _disposed) throw new PluginException(PluginErrorCode.NotReady, "插件安装尚未就绪。");
        await DiscoverAsync();
        var manifest = prepared.Manifest;
        var target = _paths.Code(manifest.SourceKind, manifest.Id);
        var backup = target + ".bak";
        Entry? previous = null, installed = null;
        var switched = false;
        var wasRemoved = _store.IsPluginRemoved(manifest.Id);
        var wasEnabled = false;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        await _operations.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            PluginPaths.CheckLinks(target);
            previous = Entries.FirstOrDefault(e => e.Registration.Definition.Id == manifest.Id);
            wasEnabled = previous?.Registration.Definition.IsEnabled(_store.Current) == true;
            if (previous is not null && previous.Registration.Definition.Metadata?.External is not { } existing)
                throw new PluginException(PluginErrorCode.IdConflict, "插件标识与现有插件冲突。");
            if (previous?.Registration.Definition.Metadata?.External is { } external && !previous.Removed
                && external.Manifest.SourceKind != manifest.SourceKind) throw new PluginException(PluginErrorCode.SourceConflict, "插件已从其他来源安装，请先卸载。");
            if (previous?.Registration.Definition.Metadata?.External is { } owner && !previous.Removed
                && manifest.SourceKind == PluginSourceKind.Official && owner.Manifest.DeveloperId != manifest.DeveloperId)
                throw new PluginException(PluginErrorCode.IndexMismatch, "插件开发者与已安装版本不一致。");
            if (Directory.Exists(target)) PluginInstaller.CheckVersion(manifest, PluginManifestReader.Read(target, manifest.Id), overwrite);
            if (previous is { Removed: false })
            {
                await PrepareLifecycleAsync(previous);
                Stop(previous, PluginStopReason.Updating);
                await previous.Cleanup;
                previous.Cleanup = Task.CompletedTask;
                if (previous.Registration.Definition.Metadata?.External is { } old) await old.VerifyUnloadAsync();
            }
            token.ThrowIfCancellationRequested();
            await Task.Run(() =>
            {
                PluginPaths.CheckTree(prepared.Directory);
                PluginPaths.CheckLinks(backup);
                PluginPaths.DeleteTree(backup);
                if (Directory.Exists(target)) { PluginPaths.CheckTree(target); Directory.Move(target, backup); }
                try { Directory.Move(prepared.Directory, target); }
                catch { if (Directory.Exists(backup)) Directory.Move(backup, target); throw; }
            }, token);
            switched = true;
            token.ThrowIfCancellationRequested();
            _store.MarkExternalRemoved(manifest.Id, false);
            // 安装只放置和验证文件；无论来源和旧版开启状态，新载荷都须由用户主动启用。
            _store.SetPluginBoolean(manifest.Id, "Host.Enabled", false);
            installed = new Entry(_loader.Registration(manifest, target)) { PreviousVersion = previous is { Removed: false } ? previous.Registration.Definition.Metadata?.Version : null };
            if (previous is not null) RemoveEntry(previous);
            AddEntry(installed);
        }
        catch (Exception error)
        {
            if (switched) await RollbackAsync();
            failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error);
        }
        finally { _operations.Release(); }
        if (failure is not null)
        {
            if (_started) await ReconcileAsync();
            failure.Throw();
        }
        try
        {
            if (_started) await ReconcileAsync();
            if (installed?.Failed == true) throw new PluginException(PluginErrorCode.Start, "插件启动失败，已恢复旧版本。");
        }
        catch
        {
            await _operations.WaitAsync();
            try { await RollbackAsync(); }
            finally { _operations.Release(); }
            if (_started) await ReconcileAsync();
            throw;
        }

        async Task RollbackAsync()
        {
            if (installed is not null)
            {
                Stop(installed);
                await installed.Cleanup;
                installed.Cleanup = Task.CompletedTask;
                RemoveEntry(installed);
                if (installed.Registration.Definition.Metadata?.External is { } failed)
                {
                    failed.Unload();
                    await failed.VerifyUnloadAsync();
                }
            }
            await Task.Run(() =>
            {
                PluginPaths.DeleteTree(target);
                if (Directory.Exists(backup)) { PluginPaths.CheckTree(backup); Directory.Move(backup, target); }
            });
            _store.MarkExternalRemoved(manifest.Id, wasRemoved);
            var restoreEnabled = wasEnabled && previous?.Registration.Definition.Metadata?.External is { } original
                && await Task.Run(() => PluginConsentPolicy.HasConsent(original));
            _store.SetPluginBoolean(manifest.Id, "Host.Enabled", restoreEnabled);
            if (previous is not null && !Entries.Contains(previous)) AddEntry(previous);
            if (previous is not null) previous.Failed = false;
        }
    }
}
