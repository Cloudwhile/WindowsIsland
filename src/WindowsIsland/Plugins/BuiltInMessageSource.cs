using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

internal abstract class BuiltInMessageSource(string id, string displayName) : IMessageSource,
    IPluginStatusProvider, IPluginPolling, IPluginNotificationFilter
{
    private IPluginContext? _context;
    private bool _disposed, _started;
    protected IMessageSourceBackend? Backend { get; private set; }
    public string Id => id;
    public string DisplayName => displayName;
    public PluginStatus Status => Backend?.Status ?? new(PluginConnectionState.NotRunning);
    // 宿主订阅事件时走事件通道，否则直接提交上下文；一次通知只使用一个通道。
    public event Action<PluginNotification>? NotificationReceived;

    public Task InitializeAsync(IPluginContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_context is not null) return Task.CompletedTask;
        if (context.PluginId != Id) throw new ArgumentException("Plugin context identity mismatch.", nameof(context));
        _context = context;
        return Task.CompletedTask;
    }

    public virtual async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        Backend = (_context ?? throw new InvalidOperationException("Plugin is not initialized."))
            .GetService<IMessageSourceBackend>() ?? throw new NotSupportedException("Source backend is unavailable.");
        _started = true;
        Backend.NotificationReceived += Publish;
        await Backend.StartAsync();
    }

    protected virtual void Publish(PluginNotification notification)
    {
        if (_disposed || !_started) return;
        if (NotificationReceived is { } received) received(notification);
        else _context?.PublishNotification(notification);
    }

    public Task PollAsync() => !_disposed && _started ? Backend?.PollAsync() ?? Task.CompletedTask : Task.CompletedTask;
    public bool Allows(PluginNotification notification) => !_disposed && _started && Backend?.Allows(notification) == true;

    public virtual Task StopAsync()
    {
        _started = false;
        var backend = Backend;
        Backend = null;
        if (backend is not null)
        {
            backend.NotificationReceived -= Publish;
            backend.Dispose();
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopAsync().GetAwaiter().GetResult(); }
        finally { _context = null; NotificationReceived = null; }
    }
}
