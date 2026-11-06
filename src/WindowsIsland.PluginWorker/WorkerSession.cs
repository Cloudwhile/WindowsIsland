using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.PluginIsolation;

namespace WindowsIsland.PluginWorker;

internal sealed class WorkerSession : IDisposable
{
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _descriptions = new();
    private long _descriptionRevision;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<IsolationRpc> _attached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WorkerLoadContext? _loadContext;
    private WorkerContext? _context;
    private IIslandPlugin? _plugin;
    private bool _ready, _initialized, _started;
    private int _disposed;

    public void Attach(IsolationRpc rpc) => _attached.TrySetResult(rpc);

    public async Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken token)
    {
        // 同步设置 API 可以在构造/初始化时重入 RPC，不能占住管道读循环。
        await Task.Yield();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        if (method == "describe")
        {
            linked.Token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!_initialized) throw new InvalidOperationException("Worker is not initialized.");
            // Callbacks can arrive during a lifecycle command or flush. A status read must not wait on that command.
            return Describe();
        }
        await _operations.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (method == "ready")
            {
                if (_initialized) throw new InvalidOperationException();
                _ready = true;
                return true;
            }
            if (method == "initialize")
            {
                if (!_ready || _initialized || _plugin is not null) throw new InvalidOperationException("Worker is not ready or is already initialized.");
                var initialization = Read<WorkerInitialization>(payload);
                if (!PluginId.IsValid(initialization.Manifest.Id) || initialization.Granted.Length > 32
                    || initialization.Manifest.ContractVersion != PluginContract.Version)
                    throw new InvalidDataException("Invalid worker initialization.");
                if (!Path.IsPathFullyQualified(initialization.CodeDirectory)) throw new InvalidDataException();
                _loadContext = new WorkerLoadContext(initialization.CodeDirectory, initialization.Manifest.EntryAssembly);
                _context = new WorkerContext(await _attached.Task.WaitAsync(linked.Token).ConfigureAwait(false), initialization, _lifetime.Token);
                try
                {
                    _plugin = _loadContext.Create(initialization.Manifest);
                    if (_plugin is IMessageSource source) source.NotificationReceived += OnNotification;
                    await _plugin.InitializeAsync(_context).WaitAsync(linked.Token).ConfigureAwait(false);
                    _initialized = true;
                }
                catch
                {
                    Dispose();
                    throw;
                }
                return Describe();
            }

            var plugin = _plugin;
            if (!_initialized || plugin is null) throw new InvalidOperationException("Worker is not initialized.");
            switch (method)
            {
                case "start":
                    if (!_started)
                    {
                        await plugin.StartAsync().WaitAsync(linked.Token).ConfigureAwait(false);
                        _started = true;
                    }
                    break;
                case "stop":
                    _context?.Revoke();
                    await plugin.StopAsync().WaitAsync(linked.Token).ConfigureAwait(false);
                    _started = false;
                    break;
                case "flush":
                    if (!_started) throw new InvalidOperationException("Worker is not running.");
                    await _context!.FlushAsync(linked.Token).ConfigureAwait(false);
                    break;
                case "poll":
                    if (_started && plugin is IPluginPolling polling)
                        await polling.PollAsync().WaitAsync(linked.Token).ConfigureAwait(false);
                    break;
                case "retry":
                    if (plugin is IPluginRetry { CanRetry: true } retry) retry.Retry();
                    break;
                case "stopping":
                    _context?.Revoke();
                    var reason = Read<PluginStopReason>(payload);
                    if (!Enum.IsDefined(reason)) throw new InvalidDataException("Invalid stop reason.");
                    if (plugin is IPluginLifecycleAware lifecycle)
                        await lifecycle.OnStoppingAsync(reason).WaitAsync(linked.Token).ConfigureAwait(false);
                    break;
                case "uninstall":
                    if (plugin is IPluginLifecycleAware uninstall)
                        await uninstall.OnUninstallAsync().WaitAsync(linked.Token).ConfigureAwait(false);
                    break;
                case "interaction":
                    if (!_started) throw new InvalidOperationException("Worker is not running.");
                    await _context!.InteractAsync(Read<PinnedControlInteraction>(payload)).WaitAsync(linked.Token).ConfigureAwait(false);
                    break;
                case "settingsChanged":
                    var keys = Read<string[]>(payload);
                    if (keys.Length > 64 || keys.Any(key => string.IsNullOrWhiteSpace(key) || key.Length > 128)) throw new InvalidDataException();
                    if (_started && plugin is IPluginSettingsChanged handler)
                        await handler.OnSettingsChangedAsync(keys).WaitAsync(linked.Token).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidDataException("Unknown worker operation.");
            }
            return Describe();
        }
        finally { _operations.Release(); }
    }

    private WorkerDescription Describe()
    {
        lock (_descriptions) return DescribeCore();
    }

    private WorkerDescription DescribeCore()
    {
        var status = (_plugin as IPluginStatusProvider)?.Status
            ?? new PluginStatus(_started ? PluginConnectionState.Connected : PluginConnectionState.NotRunning);
        if (!Enum.IsDefined(status.State) || !Enum.IsDefined(status.Reason))
            throw new InvalidDataException("Invalid plugin status.");
        var settings = new List<PluginSetting>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in (_plugin as IPluginSettingsSchema)?.SettingsSchema ?? [])
        {
            if (settings.Count >= 64 || string.IsNullOrWhiteSpace(field.Key) || field.Key.Length > 128 || !keys.Add(field.Key)
                || string.IsNullOrWhiteSpace(field.Title) || field.Title.Length > 256
                || (field.Type == PluginSettingType.String ? field.DefaultValue is not null and not string
                    : field.Type != PluginSettingType.Boolean || field.DefaultValue is not bool)
                || field.DefaultValue is string text && text.Length > 16384)
                throw new InvalidDataException("Invalid plugin settings schema.");
            settings.Add(field);
        }
        var rules = (_plugin as IPluginConversationPolicy)?.ConversationRules.ToArray() ?? [];
        if (rules.Length > 1024 || rules.Any(rule => rule is null || string.IsNullOrWhiteSpace(rule.AppId) || rule.AppId.Length > 256
            || string.IsNullOrWhiteSpace(rule.Conversation) || rule.Conversation.Length > 4096 || rule.ProcessId is <= 0))
            throw new InvalidDataException("Invalid conversation policy.");
        return new(status, settings.ToArray(), rules, ++_descriptionRevision);
    }

    private void OnNotification(PluginNotification notification) => _context?.PublishNotification(notification);
    private static T Read<T>(JsonElement value) => value.Deserialize<T>(IsolationRpc.Json)
        ?? throw new InvalidDataException("Invalid worker request.");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _context?.Dispose();
        if (_plugin is IMessageSource source)
            try { source.NotificationReceived -= OnNotification; } catch { }
        var plugin = Interlocked.Exchange(ref _plugin, null);
        // 不让不合作的 Dispose 阻塞管道断开；宿主 Job 负责最终进程回收。
        if (plugin is not null) _ = Task.Run(() => { try { plugin.Dispose(); } catch { } });
        _loadContext?.Unload();
        _loadContext = null;
    }
}
