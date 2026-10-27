using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.PluginIsolation;

namespace WindowsIsland.Plugins;

// The only object representing external code in the host. It never loads the plugin assembly.
internal sealed class SandboxPluginProxy(ExternalPlugin external) : IMessageSource, IPluginStatusProvider,
    IPluginSettingsSchema, IPluginPolling, IPluginRetry, IPluginLifecycleAware, IPluginSettingsChanged, IPluginNotificationFilter
{
    private SandboxProcess? _process;
    private IsolationRpc? _rpc;
    private IPluginContext? _context;
    private PluginNetworkBroker? _network;
    private PluginRunSnapshot? _snapshot;
    private IPluginDesktop? _desktop;
    private readonly SemaphoreSlim _desktopCommands = new(1, 1);
    private long _descriptionAt;
    private readonly CancellationTokenSource _services = new();
    private readonly CancellationTokenSource _regularCommands = new();
    private readonly CancellationTokenSource _commandLifetime = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly object _lifecycle = new();
    private readonly TaskCompletionSource _terminated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _cleanupBudget;
    private Task<SandboxProcess>? _launch;
    private int _revoked, _disposed;
    private WorkerDescription _description = new(new(PluginConnectionState.Disconnected), []);
    public string Id => external.Manifest.Id;
    public string DisplayName => external.Manifest.Name;
    public PluginStatus Status => Volatile.Read(ref _description).Status;
    public IReadOnlyList<PluginSetting> SettingsSchema => Volatile.Read(ref _description).Settings;
    public bool CanRetry => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _revoked) == 0;
    public Task Termination => _terminated.Task;
    public Task OnSettingsChangedAsync(IReadOnlyList<string> keys) => CommandAsync("settingsChanged", keys);
    public event Action<PluginNotification>? NotificationReceived;

    public bool Allows(PluginNotification notification)
    {
        if (_desktop is null) return true;
        if (Volatile.Read(ref _revoked) != 0 || Volatile.Read(ref _disposed) != 0
            || System.Diagnostics.Stopwatch.GetElapsedTime(Volatile.Read(ref _descriptionAt)) > TimeSpan.FromSeconds(10)) return false;
        var identity = WindowsIsland.Services.MessengerIdentity.FromNotification(PluginNotificationBridge.ToHost(notification));
        var rules = Volatile.Read(ref _description).ConversationRules ?? [];
        var matches = rules.Where(rule => rule.AppId == identity && rule.Conversation == notification.ConversationName
            && (notification.OriginProcessId is null || rule.ProcessId is null || rule.ProcessId == notification.OriginProcessId)).ToArray();
        return matches.Length > 0 && !matches.Any(rule => rule.Muted);
    }

    public async Task InitializeAsync(IPluginContext context)
    {
        Task<SandboxProcess> launch;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0 || _revoked != 0, this);
            if (_launch is not null) throw new InvalidOperationException();
            _context = context;
            _desktop = PluginDesktopPolicy.IsAllowed(external.Manifest) ? context.GetService<IPluginDesktop>() : null;
            launch = _launch = Task.Run(() =>
            {
                _regularCommands.Token.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _revoked) != 0 || Volatile.Read(ref _disposed) != 0)
                    throw new OperationCanceledException("The plugin was stopped before its worker launch.");
                _snapshot = PluginRunSnapshot.Open(external);
                _network = new(external, () => Volatile.Read(ref _revoked) == 0 && Volatile.Read(ref _disposed) == 0,
                    snapshot: _snapshot);
                return new SandboxProcess(new(external.Manifest, external.HostRoot, external.Directory, external.DataDirectory));
            });
        }
        SandboxProcess process;
        try { process = await launch.WaitAsync(_regularCommands.Token); }
        catch { Dispose(); throw; }
        var revoked = false;
        lock (_lifecycle)
        {
            revoked = _disposed != 0 || _revoked != 0;
            if (!revoked)
            {
                _process = process;
                _rpc = new(process.Input, process.Output, HandleAsync);
                context.PinnedContent?.SetInteractionHandler(interaction => CommandAsync("interaction", interaction));
            }
        }
        if (revoked)
        {
            Dispose();
            await Termination;
            throw new OperationCanceledException("The plugin was stopped while its worker was starting.");
        }
        try { await process.VerifyReadyAsync(_rpc!, _regularCommands.Token); }
        catch { Dispose(); throw; }
        await CommandAsync("initialize", new WorkerInitialization(external.Manifest, external.DataDirectory,
            external.Trust.Granted.ToArray(), context.PreviousVersion, context.HostVersion, external.Directory));
    }

    private async Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken token)
    {
        if (_revoked != 0 || _disposed != 0 || _context is not { } context) throw new ObjectDisposedException(nameof(SandboxPluginProxy));
        T Read<T>() => payload.Deserialize<T>(IsolationRpc.Json) ?? throw new InvalidDataException();
        switch (method)
        {
            case "getString":
            {
                var field = Read<WorkerSetting>();
                return context.Settings.GetString(field.Key, field.Value);
            }
            case "getBoolean":
            {
                var field = Read<WorkerSetting>();
                return context.Settings.GetBoolean(field.Key, field.Boolean);
            }
            case "setString":
            {
                var field = Read<WorkerSetting>();
                if (field.Value?.Length > 65536) throw new InvalidDataException();
                context.Settings.SetString(field.Key, field.Value); return null;
            }
            case "setBoolean":
            {
                var field = Read<WorkerSetting>(); context.Settings.SetBoolean(field.Key, field.Boolean); return null;
            }
            case "diagnostic": context.Diagnostics.Write(Read<PluginDiagnostic>()); return null;
            case "notification":
            {
                var value = Read<PluginNotification>();
                if (value.SourceId != Id || value.ConversationName?.Length > 4096 || value.Sender?.Length > 4096
                    || value.Body?.Length > 65536 || value.AppIcon?.Length > 256 * 1024 || value.SenderAvatar?.Length > 256 * 1024
                    || value.DeduplicationKey?.Length > 1024 || value.AppName?.Length > 256 || value.AppId?.Length > 256
                    || value.Symbol?.Length > 64 || !Enum.IsDefined(value.Kind)) throw new InvalidDataException();
                var target = value.OriginProcessId is { } processId && _desktop is IPluginDesktopProcessScope scope && scope.OwnsProcess(processId)
                    ? value.OriginProcessId : null;
                NotificationReceived?.Invoke(value with { OriginProcessId = target }); return null;
            }
            case "pinned":
            {
                var value = Read<WorkerPinnedChange>();
                var pinned = context.PinnedContent ?? throw new UnauthorizedAccessException();
                if (value.Kind == "Removed") { if (value.Id.Length > 128) throw new InvalidDataException(); pinned.Remove(value.Id); return null; }
                var content = value.Content ?? throw new InvalidDataException();
                content = PinnedContentPolicy.Normalize(content, Id, reviewed: false, () => { });
                if (value.Kind == "Added") pinned.Submit(content);
                else if (value.Kind == "Updated") pinned.Update(content);
                else throw new InvalidDataException();
                return null;
            }
            case "desktop":
            {
                var service = _desktop ?? throw new UnauthorizedAccessException();
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _services.Token);
                await _desktopCommands.WaitAsync(cancellation.Token);
                try
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (_snapshot?.IsCurrent() != true) throw new UnauthorizedAccessException();
                    var request = Read<WorkerDesktopRequest>();
                    return await DesktopRpc.DispatchAsync(service, request, cancellation.Token);
                }
                finally { _desktopCommands.Release(); }
            }
            case "network":
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _services.Token);
                return await _network!.SendAsync(Read<PluginNetworkRequest>(), cancellation.Token);
            }
            default: throw new InvalidDataException("Unknown sandbox operation.");
        }
    }

    private async Task CommandAsync(string method, object? payload = null)
    {
        var cleanup = method is "stop" or "stopping" or "uninstall";
        CancellationToken scope;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            scope = cleanup ? (_cleanupBudget ??= new CancellationTokenSource(TimeSpan.FromSeconds(3))).Token : _regularCommands.Token;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(scope, _commandLifetime.Token);
        if (!cleanup) timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var entered = false;
        Task<WorkerDescription?>? reply = null;
        try
        {
            await _commands.WaitAsync(timeout.Token);
            entered = true;
            IsolationRpc rpc;
            lock (_lifecycle)
            {
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                rpc = _rpc ?? throw new InvalidOperationException();
            }
            reply = rpc.CallAsync<WorkerDescription>(method, payload, timeout.Token);
            var description = await reply.WaitAsync(timeout.Token)
                ?? throw new InvalidDataException();
            if (description.Status is null || !Enum.IsDefined(description.Status.State) || !Enum.IsDefined(description.Status.Reason)
                || description.Settings is null || description.Settings.Length > 128) throw new InvalidDataException();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var fields = description.Settings.Select(field =>
            {
                if (field is null || string.IsNullOrWhiteSpace(field.Key) || field.Key.Length > 128 || !keys.Add(field.Key)
                    || string.IsNullOrWhiteSpace(field.Title) || field.Title.Length > 512 || !Enum.IsDefined(field.Type)) throw new InvalidDataException();
                object? value = field.DefaultValue;
                if (value is JsonElement json) value = json.ValueKind switch
                {
                    JsonValueKind.String when field.Type == PluginSettingType.String => json.GetString(),
                    JsonValueKind.True or JsonValueKind.False when field.Type == PluginSettingType.Boolean => json.GetBoolean(),
                    JsonValueKind.Null when field.Type == PluginSettingType.String => null,
                    _ => throw new InvalidDataException()
                };
                if (value is string text && text.Length > 65536) throw new InvalidDataException();
                return field with { DefaultValue = value };
            }).ToArray();
            var rules = description.ConversationRules;
            if (rules is { Length: > 1024 } || rules?.Any(rule => rule is null || string.IsNullOrWhiteSpace(rule.AppId) || rule.AppId.Length > 256
                || string.IsNullOrWhiteSpace(rule.Conversation) || rule.Conversation.Length > 4096 || rule.ProcessId is <= 0) == true)
                throw new InvalidDataException();
            Volatile.Write(ref _descriptionAt, System.Diagnostics.Stopwatch.GetTimestamp());
            Volatile.Write(ref _description, description with { Settings = fields, ConversationRules = rules?.ToArray() });
        }
        catch (OperationCanceledException) when (!cleanup && Volatile.Read(ref _revoked) != 0) { throw; }
        catch { Dispose(); throw; }
        finally
        {
            if (reply is { IsCompleted: false }) ObserveLateFailure(reply);
            if (entered) _commands.Release();
        }
    }

    public Task StartAsync() => CommandAsync("start");
    public Task PollAsync() => CommandAsync("poll");
    public async void Retry() { try { await CommandAsync("retry"); } catch { } }
    public Task OnStoppingAsync(PluginStopReason reason) { Revoke(); return CleanupCommandAsync("stopping", reason); }
    public Task OnUninstallAsync() { Revoke(); return CleanupCommandAsync("uninstall"); }
    public Task StopAsync() { Revoke(); return CleanupCommandAsync("stop"); }

    private Task CleanupCommandAsync(string method, object? payload = null)
    {
        lock (_lifecycle)
            return _rpc is null || _disposed != 0 ? Task.CompletedTask : CommandAsync(method, payload);
    }

    public void Revoke()
    {
        if (Interlocked.Exchange(ref _revoked, 1) != 0) return;
        lock (_lifecycle) _cleanupBudget ??= new CancellationTokenSource(TimeSpan.FromSeconds(3));
        _services.Cancel();
        _regularCommands.Cancel();
    }

    public void Dispose()
    {
        Task<SandboxProcess>? launch;
        SandboxProcess? process;
        lock (_lifecycle)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Revoke();
            _commandLifetime.Cancel();
            _rpc?.Dispose();
            _rpc = null;
            _context = null;
            launch = _launch;
            process = _process;
            NotificationReceived = null;
        }
        _ = CompleteTerminationAsync(launch, process);
    }

    private async Task CompleteTerminationAsync(Task<SandboxProcess>? launch, SandboxProcess? process)
    {
        var cleanup = KillAndWaitAsync(launch, process);
        try
        {
            await cleanup.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            _terminated.TrySetResult();
        }
        catch (Exception error)
        {
            _terminated.TrySetException(error);
            // A slow native launch still has an owner: kill it when it eventually completes.
            ObserveLateFailure(cleanup);
        }
    }

    private async Task KillAndWaitAsync(Task<SandboxProcess>? launch, SandboxProcess? process)
    {
        try
        {
            if (process is null && launch is not null)
            {
                try { process = await launch.ConfigureAwait(false); }
                catch (Exception error) when (error is not TimeoutException) { return; }
            }
            if (process is null) return;
            process.Dispose();
            await process.ExitTask.ConfigureAwait(false);
        }
        finally { Interlocked.Exchange(ref _snapshot, null)?.Dispose(); }
    }

    private static void ObserveLateFailure(Task task) => _ = task.ContinueWith(completed => _ = completed.Exception, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
