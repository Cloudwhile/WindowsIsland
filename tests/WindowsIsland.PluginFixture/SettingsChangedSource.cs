using WindowsIsland.Contracts;

namespace WindowsIsland.PluginFixture;

public sealed class SettingsChangedSource : IIslandPlugin, IPluginSettingsChanged
{
    private string? _log;
    private IPluginContext? _context;
    public string Id => "wini_plugin_fixture";
    public string DisplayName => "Settings fixture";
    public Task InitializeAsync(IPluginContext context)
    {
        _context = context;
        _log = Path.Combine(context.GetService<IPluginStorage>()!.DataDirectory, "changes.txt");
        if (context.Settings.GetString("token") != "synthetic-private-token") throw new InvalidDataException();
        File.AppendAllText(_log, "Initialize\n");
        return Task.CompletedTask;
    }
    public Task OnSettingsChangedAsync(IReadOnlyList<string> keys)
    {
        if (_context!.Settings.GetString("count") != "7") throw new InvalidDataException();
        File.AppendAllText(_log!, "Changed:" + string.Join(',', keys.Order()) + "\n");
        return Task.CompletedTask;
    }
    public Task StartAsync() => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public void Dispose() { }
}
