using WindowsIsland.Contracts;
namespace HelloNotification;

public sealed class Plugin : IIslandPlugin
{
    private IPluginContext? _context;
    private CancellationTokenSource? _shutdown;
    private Task? _run;
    public string Id => "wini_plugin_hellonotification";
    public string DisplayName => "HelloNotification";
    public Task InitializeAsync(IPluginContext context) { _context = context; return Task.CompletedTask; }
    public Task StartAsync()
    {
        if (_run is not null) return Task.CompletedTask;
        var context = _context ?? throw new InvalidOperationException("尚未初始化");
        _shutdown = new CancellationTokenSource();
        _run = RunAsync(context, _shutdown.Token);
        return Task.CompletedTask;
    }
    private async Task RunAsync(IPluginContext context, CancellationToken token)
    {
        try
        {
            await Task.Delay(1000, token);
            if (!context.Settings.GetBoolean("enabled", true)) return;
            token.ThrowIfCancellationRequested();
            context.PublishNotification(new PluginNotification(Id, "示例通知", null, "你好，消息岛", DateTimeOffset.UtcNow,
                DeduplicationKey: Guid.NewGuid().ToString("N"), AppName: DisplayName));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            context.Diagnostics.Write(new PluginDiagnostic("Plugin", "Run", AdditionalInformation:
                new PluginDiagnosticInfo(error.GetType().FullName, error.HResult)));
        }
    }
    public async Task StopAsync()
    {
        _shutdown?.Cancel();
        try { if (_run is not null) await _run; }
        finally
        {
            _context?.PinnedContent?.SetInteractionHandler(null);
            _context?.PinnedContent?.Remove("counter");
            _shutdown?.Dispose(); _shutdown = null; _run = null;
        }
    }
    public void Dispose()
    {
        // 生命周期要求先 StopAsync；直接释放也取消尚未完成的任务。
        _shutdown?.Cancel(); _shutdown?.Dispose(); _shutdown = null;
        _context?.PinnedContent?.SetInteractionHandler(null);
        _context?.PinnedContent?.Remove("counter");
        _context = null;
    }
}
