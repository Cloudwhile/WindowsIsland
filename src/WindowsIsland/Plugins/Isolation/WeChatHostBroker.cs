using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

// 只暴露三个固定操作。所有平台资源及消息留在宿主，由生命周期所属线程串行访问。
internal sealed class WeChatHostBroker(Func<IMessageSourceBackend?> create, Action<Action> dispatch,
    Action<PluginNotification> publish) : IWeChatHost, IDisposable
{
    private readonly string _session = Guid.NewGuid().ToString("N");
    private IMessageSourceBackend? _backend;
    private int _disposed;

    public static bool IsOfficial(PluginManifest manifest) => manifest.Id == BuiltInPluginIds.WeChat
        && manifest.SourceKind == PluginSourceKind.Official
        && manifest.DeveloperId == PluginCatalogClient.OfficialDeveloperId
        && string.Equals(manifest.RepositoryUrl?.TrimEnd('/'), "https://github.com/WinIOfficial/wini_plugin_wechat", StringComparison.OrdinalIgnoreCase)
        && manifest.Permissions.Contains(PluginPermissions.WeChat, StringComparer.Ordinal)
        && manifest.Permissions.Contains(PluginPermissions.Notifications, StringComparer.Ordinal);

    public static bool IsOfficial(PluginCatalogEntry entry) => IsOfficial(new PluginManifest(entry.Id, entry.Name, entry.Version,
        "unused.dll", "Unused", entry.ContractVersion, entry.Permissions, DeveloperId: entry.DeveloperId,
        RepositoryUrl: entry.RepositoryUrl, SourceKind: PluginSourceKind.Official));

    public Task<WeChatHostStatus> StartAsync() => ExecuteAsync(async () =>
    {
        if (_backend is not null) return;
        _backend = create() ?? throw new NotSupportedException("WeChat host service is unavailable.");
        _backend.NotificationReceived += Publish;
        await _backend.StartAsync();
    });

    public Task<WeChatHostStatus> PollAsync() => ExecuteAsync(() => _backend?.PollAsync() ?? Task.CompletedTask);
    public Task<WeChatHostStatus> RetryAsync() => ExecuteAsync(() =>
    {
        if (_backend is IPluginRetry { CanRetry: true } retry) retry.Retry();
        return Task.CompletedTask;
    });

    private Task<WeChatHostStatus> ExecuteAsync(Func<Task> action)
    {
        if (Volatile.Read(ref _disposed) != 0) return Task.FromException<WeChatHostStatus>(new ObjectDisposedException(nameof(WeChatHostBroker)));
        var completion = new TaskCompletionSource<WeChatHostStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            dispatch(async () =>
            {
                try
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                    await action();
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                    completion.TrySetResult(new(_backend?.Status ?? new(PluginConnectionState.NotRunning),
                        _backend is IPluginRetry { CanRetry: true }));
                }
                catch (Exception error) { completion.TrySetException(error); }
            });
        }
        catch (Exception error) { completion.TrySetException(error); }
        return completion.Task;
    }

    private void Publish(PluginNotification notification)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        publish(notification is { Kind: PluginNotificationKind.ClientAutomation, DeduplicationKey: { } key }
            ? notification with { DeduplicationKey = _session + "/" + key } : notification);
    }

    public bool Allows(PluginNotification notification) => Volatile.Read(ref _disposed) == 0 && _backend?.Allows(notification) == true;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var backend = _backend;
        _backend = null;
        if (backend is null) return;
        backend.NotificationReceived -= Publish;
        backend.Dispose();
    }
}
