using WindowsIsland.Contracts;

namespace WindowsIsland.PluginFixture;

// Lifecycle-only fixture also verifies that the host adapter forwards optional interfaces.
public sealed class SchemaSource : IIslandPlugin, IPluginSettingsSchema, IPluginLifecycleAware
{
    private string? _log;
    private string? _code;
    private bool _failUninstall;
    public string Id => "wini_plugin_fixture";
    public string DisplayName => "Schema fixture";
    public IReadOnlyList<PluginSetting> SettingsSchema =>
    [
        new("message", PluginSettingType.String, "Fixture message", "default message"),
        new("enabled", PluginSettingType.Boolean, "Fixture enabled", true)
    ];
    public Task InitializeAsync(IPluginContext context)
    {
        _log = Path.Combine(context.GetService<IPluginStorage>()!.DataDirectory, "lifecycle.txt");
        _code = GetType().Assembly.Location;
        _failUninstall = context.Settings.GetBoolean("failUninstall");
        Log("Initialize:" + (context.PreviousVersion ?? "null") + ":" + context.HostVersion);
        Log("Settings:" + context.Settings.GetString("message", "default message") + ":" + context.Settings.GetBoolean("enabled", true));
        return Task.CompletedTask;
    }
    public Task StartAsync() { Log("Start"); return Task.CompletedTask; }
    public Task OnStoppingAsync(PluginStopReason reason) { Log("Stopping:" + reason); return Task.CompletedTask; }
    public Task StopAsync() { Log("Stop"); return Task.CompletedTask; }
    public Task OnUninstallAsync()
    {
        Log("Uninstall:" + File.Exists(_code));
        if (_failUninstall) throw new InvalidOperationException("Fixture uninstall failure");
        return Task.CompletedTask;
    }
    public void Dispose() => Log("Dispose");
    private void Log(string value) { if (_log is not null) File.AppendAllText(_log, value + Environment.NewLine); }
}
