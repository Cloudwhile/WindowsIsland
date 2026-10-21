using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

internal sealed partial class PluginManager
{
    private PluginDirectoryLoader? _loader;
    private PluginPaths? _paths;
    private readonly object _discoveryGate = new();
    private Task? _discovery;
    public void ConfigureDirectories(PluginPaths paths) { _paths = paths; _loader = new PluginDirectoryLoader(paths, _store); }
    private Task DiscoverAsync() { lock (_discoveryGate) return _discovery ??= ScanDirectoriesAsync(); }

    /// <summary>重新扫描目录，只添加通过校验且未被用户删除的插件，再协调启停。</summary>
    public async Task RescanAsync()
    {
        if (_disposed) return;
        await DiscoverAsync();
        await _mutations.WaitAsync();
        try { await ScanDirectoriesAsync(); }
        finally { _mutations.Release(); }
        if (_started && !_disposed) await ReconcileAsync();
    }
    private async Task ScanDirectoriesAsync()
    {
        if (_loader is null) return;
        var items = await Task.Run(_loader.Scan);
        if (_disposed) return;
        await _operations.WaitAsync();
        try
        {
            if (_disposed) return;
            foreach (var registration in items)
                if (!_store.IsPluginRemoved(registration.Definition.Id)
                    && Entries.All(entry => entry.Registration.Definition.Id != registration.Definition.Id))
                    AddEntry(new Entry(registration));
        }
        finally { _operations.Release(); }
    }
}

internal sealed record PluginStorage(string DataDirectory) : IPluginStorage;
