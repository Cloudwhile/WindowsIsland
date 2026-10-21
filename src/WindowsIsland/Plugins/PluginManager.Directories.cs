using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

internal sealed partial class PluginManager
{
    private PluginDirectoryLoader? _loader;
    private PluginPaths? _paths;
    private Task? _discovery;
    public void ConfigureDirectories(PluginPaths paths) { _paths = paths; _loader = new PluginDirectoryLoader(paths, _store); }
    private Task DiscoverAsync() => _discovery ??= DiscoverCoreAsync();
    private async Task DiscoverCoreAsync()
    {
        if (_loader is null) return;
        var items = await Task.Run(_loader.Scan);
        if (_disposed) return;
        foreach (var registration in items)
            if (_entries.All(entry => entry.Registration.Definition.Id != registration.Definition.Id))
            {
                try
                {
                    if (_store.IsPluginRemoved(registration.Definition.Id)) _store.MarkExternalRemoved(registration.Definition.Id, false);
                    _entries.Add(new Entry(registration));
                }
                catch (Exception error) { new PluginDiagnostics(registration.Definition.Id, Services.DiagnosticArea.Application).Capture(error, "Discover"); }
            }
    }
}

internal sealed record PluginStorage(string DataDirectory) : IPluginStorage;
