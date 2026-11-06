using WindowsIsland.Contracts;

namespace WindowsIsland.PluginSecurityFixture;

public sealed class LifecycleSource : IIslandPlugin
{
    private string? _data;
    private bool _hangStart, _hangStop;
    public string Id => "wini_plugin_security_lifecycle";
    public string DisplayName => "Security lifecycle fixture";

    public Task InitializeAsync(IPluginContext context)
    {
        _data = context.GetService<IPluginStorage>()?.DataDirectory
            ?? throw new InvalidOperationException("Fixture storage is unavailable.");
        _hangStart = context.Settings.GetBoolean("hangStart");
        _hangStop = context.Settings.GetBoolean("hangStop");
        Write("initialized");
        return Task.CompletedTask;
    }

    public Task StartAsync() { Write("start-entered"); return _hangStart ? Never() : Task.CompletedTask; }
    public Task StopAsync() { Write("stop-entered"); return _hangStop ? Never() : Task.CompletedTask; }
    public void Dispose() => Write("disposed");
    private static Task Never() => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
    private void Write(string name)
    {
        if (_data is not null) File.WriteAllText(Path.Combine(_data, name + ".marker"), "fixed-lifecycle-canary");
    }
}

public sealed class StoppingSource : IIslandPlugin, IPluginLifecycleAware
{
    private readonly LifecycleSource _inner = new();
    private string? _data;
    private bool _hang;
    public string Id => "wini_plugin_security_stopping";
    public string DisplayName => "Security stopping fixture";
    public Task InitializeAsync(IPluginContext context)
    {
        _data = context.GetService<IPluginStorage>()?.DataDirectory;
        _hang = context.Settings.GetBoolean("hangStopping");
        return _inner.InitializeAsync(context);
    }
    public Task StartAsync() => _inner.StartAsync();
    public Task StopAsync() => _inner.StopAsync();
    public Task OnStoppingAsync(PluginStopReason reason)
    {
        File.WriteAllText(Path.Combine(_data!, "stopping-entered.marker"), "fixed-lifecycle-canary");
        return _hang ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task : Task.CompletedTask;
    }
    public void Dispose() => _inner.Dispose();
}
