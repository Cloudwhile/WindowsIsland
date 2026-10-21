using WindowsIsland.Contracts;

namespace WindowsIsland.PluginFixture;

// 与内置电源来源一样经上下文提交通知，不引用宿主或 WinUI。
public sealed class PowerSource : IMessageSource, IPluginStatusProvider
{
    private IPluginContext? _context;
    private Timer? _timer;
    public string Id => "wini_fixture_plugin";
    public string DisplayName => "外部电源来源";
    public PluginStatus Status => new(PluginConnectionState.Connected);
    public event Action<PluginNotification>? NotificationReceived { add { } remove { } }
    public Task InitializeAsync(IPluginContext context) { _context = context; return Task.CompletedTask; }
    public Task StartAsync()
    {
        _timer = new Timer(_ => _context?.PublishNotification(new(Id, "外部电源来源", null, _context.Settings.GetString("message", "外部插件通知")!, DateTimeOffset.UtcNow,
            OriginProcessId: Environment.ProcessId, Kind: PluginNotificationKind.Power)),
            null, 300, Timeout.Infinite);
        return Task.CompletedTask;
    }
    public Task StopAsync() { _timer?.Dispose(); _timer = null; return Task.CompletedTask; }
    public void Dispose() { _timer?.Dispose(); _context = null; }
}

public sealed class BrokenSource : IMessageSource
{
    public BrokenSource() => throw new InvalidOperationException("fixture constructor failure");
    public string Id => "wini_fixture_plugin";
    public string DisplayName => "启动失败夹具";
    public event Action<PluginNotification>? NotificationReceived { add { } remove { } }
    public Task InitializeAsync(IPluginContext context) => Task.CompletedTask;
    public Task StartAsync() => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public void Dispose() { }
}
