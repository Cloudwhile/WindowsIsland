using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

internal sealed class SystemNotificationPlugin() : BuiltInMessageSource(BuiltInPluginIds.System, "System notifications"), IPluginNotificationAccess
{
    private IPluginNotificationAccess? _access;
    public PluginNotificationAccess Access => _access?.Access ?? PluginNotificationAccess.Waiting;
    public event Action<PluginNotificationAccess>? AccessChanged;

    public override async Task StartAsync()
    {
        await base.StartAsync();
        if (ReferenceEquals(_access, Backend)) return;
        _access = Backend as IPluginNotificationAccess;
        if (_access is not null) _access.AccessChanged += OnAccessChanged;
    }

    public override Task StopAsync()
    {
        if (_access is not null) _access.AccessChanged -= OnAccessChanged;
        _access = null;
        AccessChanged = null;
        return base.StopAsync();
    }

    public Task RequestAccessAsync() => _access?.RequestAccessAsync() ?? Task.CompletedTask;
    private void OnAccessChanged(PluginNotificationAccess value) => AccessChanged?.Invoke(value);
}
