using WindowsIsland.Contracts;
using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

// 生命周期在界面线程串行执行，停用先撤销代次，再异步停止和释放。
internal sealed partial class PluginManager : IDisposable
{
    private sealed class Entry(NotificationPluginRegistration registration)
    {
        public readonly NotificationPluginRegistration Registration = registration;
        public IMessageSource? Plugin;
        public PluginContext? Context;
        public IMessageSourceBackend? Backend;
        public Action<PluginNotification>? Received;
        public Action<PluginNotificationAccess>? AccessChanged;
        public Task Cleanup = Task.CompletedTask;
        public PluginTrust? Trust;
        public bool Active, Failed, Removed;
    }

    private readonly List<Entry> _entries;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly SemaphoreSlim _mutations = new(1, 1);
    private readonly SettingsStore _store;
    private readonly Action<Action> _dispatch;
    private readonly Func<string, IMessageSourceBackend?>? _backends;
    private AppSettings _settings;
    private bool _started, _disposed;
    public NotificationAccess Access { get; private set; } = NotificationAccess.Waiting;
    public event Action<IslandNotification>? Received;
    public event Action<NotificationAccess>? AccessChanged;

    public PluginManager(IEnumerable<NotificationPluginRegistration> registrations, SettingsStore store,
        Action<Action>? dispatch = null, Func<string, IMessageSourceBackend?>? backends = null)
    {
        var items = registrations.ToArray();
        if (items.Any(item => !PluginId.IsValid(item.Definition.Id))
            || items.Select(item => item.Definition.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Length)
            throw new ArgumentException("Plugin identifiers must follow wini_<name>_plugin and be unique.", nameof(registrations));
        _entries = items.Select(item => new Entry(item)).ToList();
        _store = store;
        foreach (var entry in _entries)
            entry.Removed = entry.Registration.Definition.CanRemove && store.IsPluginRemoved(entry.Registration.Definition.Id);
        _settings = store.Current;
        _dispatch = dispatch ?? (action => action());
        _backends = backends;
    }

    public async Task StartAsync()
    {
        if (_disposed) return;
        await DiscoverAsync();
        _started = true;
        await ReconcileAsync();
    }

    public async Task SetEnabledAsync(string id, bool enabled)
    {
        if (_disposed) return;
        var entry = _entries.FirstOrDefault(item => item.Registration.Definition.Id == id)
            ?? throw new ArgumentException("Unknown plugin identifier.", nameof(id));
        if (entry.Removed) throw new InvalidOperationException("The plugin has been removed.");
        _store.Save(entry.Registration.Definition.SetEnabled(_store.Current, enabled));
        await ApplySettingsAsync(_store.Current);
    }

    public Task ApplySettingsAsync(AppSettings settings)
    {
        if (_disposed) return Task.CompletedTask;
        var previous = _settings;
        _settings = settings;
        foreach (var entry in _entries)
            if (entry.Registration.Definition.IsEnabled(previous) && !entry.Registration.Definition.IsEnabled(settings))
            { Stop(entry); entry.Failed = false; }
        return _started ? ReconcileAsync() : Task.CompletedTask;
    }

    private async Task ReconcileAsync()
    {
        await _operations.WaitAsync();
        try
        {
            foreach (var entry in _entries)
            {
                if (_disposed) return;
                await entry.Cleanup;
                if (_disposed) return;
                if (entry.Removed || !entry.Registration.Definition.IsEnabled(_settings)) { Stop(entry); entry.Failed = false; continue; }
                if (entry.Plugin is not null || entry.Failed) continue;
                IMessageSource? starting = null;
                var operation = "Create";
                try
                {
                    var plugin = entry.Registration.Create();
                    starting = plugin;
                    entry.Plugin = plugin;
                    if (plugin.Id != entry.Registration.Definition.Id) throw new InvalidOperationException("Plugin identity mismatch.");
                    var external = entry.Registration.Definition.Metadata?.External;
                    entry.Trust = external?.Trust;
                    var pluginStore = external is null ? _store : new SettingsStore(Path.Combine(external.DataDirectory, "settings.json"));
                    entry.Context = new PluginContext(plugin.Id, new PluginSettings(pluginStore, plugin.Id),
                        Diagnostic(entry), notification => DispatchNotification(entry, plugin, notification), type =>
                        {
                            if (_disposed || !ReferenceEquals(entry.Plugin, plugin)) return null;
                            if (external is not null) return type == typeof(IPluginPermissions) ? entry.Trust
                                : type == typeof(IPluginStorage) && entry.Trust?.IsGranted(PluginPermissions.Settings) == true ? new PluginStorage(external.DataDirectory) : null;
                            if (type != typeof(IMessageSourceBackend)) return null;
                            return entry.Backend ??= _backends?.Invoke(plugin.Id);
                        });
                    if (plugin is not BuiltInMessageSource)
                    {
                        entry.Received = notification => DispatchNotification(entry, plugin, notification);
                        plugin.NotificationReceived += entry.Received;
                    }
                    if (plugin is IPluginNotificationAccess access && external is null)
                    {
                        entry.AccessChanged = value => Dispatch(entry, plugin, () => SetAccess((NotificationAccess)value));
                        access.AccessChanged += entry.AccessChanged;
                    }
                    operation = "Initialize";
                    await plugin.InitializeAsync(entry.Context);
                    if (_disposed || !ReferenceEquals(entry.Plugin, plugin)) continue;
                    operation = "Start";
                    await plugin.StartAsync();
                    if (_disposed || !ReferenceEquals(entry.Plugin, plugin)) continue;
                    entry.Active = true;
                    if (plugin is IPluginNotificationAccess initialized && external is null) SetAccess((NotificationAccess)initialized.Access);
                }
                catch (Exception error)
                {
                    if (!_disposed && (starting is null || ReferenceEquals(entry.Plugin, starting))) Fail(entry, error, operation);
                }
            }
        }
        finally { _operations.Release(); }
    }

    private void DispatchNotification(Entry entry, IMessageSource plugin, PluginNotification notification) => Dispatch(entry, plugin, () =>
    {
        if (!entry.Active || !entry.Registration.Definition.IsEnabled(_settings)) return;
        if (entry.Trust is { } trust)
        {
            if (!trust.IsGranted(PluginPermissions.Notifications)) return;
            if (!trust.IsGranted(PluginPermissions.Process)) notification = notification with { OriginProcessId = null };
        }
        if (notification.SourceId != entry.Registration.Definition.Id) throw new InvalidOperationException("Notification source identity mismatch.");
        Received?.Invoke(PluginNotificationBridge.ToHost(notification) with
        { AllowActivation = entry.Trust?.IsGranted(PluginPermissions.Process) ?? true });
    });

    private void Dispatch(Entry entry, IMessageSource plugin, Action action)
    {
        try
        {
            _dispatch(() =>
            {
                if (_disposed || !ReferenceEquals(entry.Plugin, plugin)) return;
                try { action(); }
                catch (Exception error) { if (ReferenceEquals(entry.Plugin, plugin)) Fail(entry, error, "Callback"); }
            });
        }
        catch (Exception error) { if (!_disposed && ReferenceEquals(entry.Plugin, plugin)) Fail(entry, error, "Dispatch"); }
    }

    public async Task PollAsync()
    {
        if (_disposed || !_started || !await _operations.WaitAsync(0)) return;
        try
        {
            foreach (var entry in _entries)
            {
                if (_disposed) return;
                if (!entry.Active || entry.Plugin is not IPluginPolling polling) continue;
                var plugin = entry.Plugin;
                try { await polling.PollAsync(); }
                catch (Exception error) { if (ReferenceEquals(entry.Plugin, plugin)) Fail(entry, error, "Poll"); }
            }
        }
        finally { _operations.Release(); }
    }

    public async Task RequestAccessAsync()
    {
        if (_disposed) return;
        await ReconcileAsync();
        await _operations.WaitAsync();
        try
        {
            if (_disposed) return;
            foreach (var entry in _entries)
                if (entry.Plugin is IPluginNotificationAccess access && entry.Registration.Definition.Metadata?.External is null)
                {
                    try { await access.RequestAccessAsync(); }
                    catch (Exception error) { if (ReferenceEquals(entry.Plugin, access)) Fail(entry, error, "RequestAccess"); }
                }
        }
        finally { _operations.Release(); }
    }

    public IReadOnlyList<NotificationPluginStatus> Snapshot() => _entries.Where(entry => !entry.Removed).Select(entry =>
    {
        var key = "Off";
        var retry = false;
        PluginStatus? status = null;
        if (!_disposed && entry.Registration.Definition.IsEnabled(_settings))
        {
            try
            {
                status = entry.Failed ? new(PluginConnectionState.Disconnected, PluginStatusReason.Unavailable)
                    : (entry.Plugin as IPluginStatusProvider)?.Status ?? new(PluginConnectionState.Disconnected);
                key = PluginNotificationBridge.StatusKey(entry.Registration.Definition.Id, status);
                retry = entry.Failed || entry.Plugin is IPluginRetry { CanRetry: true };
            }
            catch (Exception error)
            {
                Fail(entry, error, "Status"); key = "Unavailable"; retry = true;
                status = new(PluginConnectionState.Disconnected, PluginStatusReason.Unavailable);
            }
        }
        return new NotificationPluginStatus(entry.Registration.Definition, key, retry, status);
    }).ToArray();

    public Task RetryAsync(string id)
    {
        if (_disposed) return Task.CompletedTask;
        var entry = _entries.FirstOrDefault(item => item.Registration.Definition.Id == id);
        if (entry is null || entry.Removed || !entry.Registration.Definition.IsEnabled(_settings)) return Task.CompletedTask;
        if (entry.Failed) { entry.Failed = false; return _started ? ReconcileAsync() : Task.CompletedTask; }
        if (entry.Plugin is IPluginRetry plugin)
        {
            try { plugin.Retry(); }
            catch (Exception error) { Fail(entry, error, "Retry"); }
        }
        return Task.CompletedTask;
    }

    public bool Allows(IslandNotification notification)
    {
        if (_disposed || !_settings.Allows(notification)) return false;
        foreach (var entry in _entries)
        {
            if (!entry.Registration.Definition.Matches(notification)) continue;
            if (!entry.Active || entry.Plugin is null) return false;
            try
            {
                if (entry.Plugin is IPluginNotificationFilter filter
                    && !filter.Allows(PluginNotificationBridge.ToContract(entry.Registration.Definition.Id, notification))) return false;
            }
            catch (Exception error) { Fail(entry, error, "Filter"); return false; }
        }
        return true;
    }

    private PluginDiagnostics Diagnostic(Entry entry) => new(entry.Registration.Definition.Id, entry.Registration.Definition.DiagnosticArea);

    private void Fail(Entry entry, Exception error, string operation)
    {
        Diagnostic(entry).Capture(error, operation);
        var access = entry.Plugin is IPluginNotificationAccess && entry.Registration.Definition.Metadata?.External is null;
        entry.Failed = true;
        Stop(entry);
        if (access && !_disposed) SetAccess(NotificationAccess.Unavailable);
    }

    private void SetAccess(NotificationAccess value)
    {
        if (Access == value) return;
        Access = value;
        AccessChanged?.Invoke(value);
    }

    private void Stop(Entry entry)
    {
        var plugin = entry.Plugin;
        var backend = entry.Backend;
        entry.Active = false;
        entry.Plugin = null;
        entry.Backend = null;
        entry.Context?.Dispose();
        entry.Context = null;
        if (plugin is null) return;
        try
        {
            plugin.NotificationReceived -= entry.Received;
            if (plugin is IPluginNotificationAccess access) access.AccessChanged -= entry.AccessChanged;
        }
        catch (Exception error) { Diagnostic(entry).Capture(error, "Detach"); }
        entry.Received = null;
        entry.AccessChanged = null;
        entry.Cleanup = CleanupAsync(entry, plugin, backend);
    }

    private async Task CleanupAsync(Entry entry, IMessageSource plugin, IMessageSourceBackend? backend)
    {
        try { await plugin.StopAsync(); }
        catch (Exception error) { Diagnostic(entry).Capture(error, "Stop"); }
        try { plugin.Dispose(); }
        catch (Exception error) { Diagnostic(entry).Capture(error, "Dispose"); }
        try { backend?.Dispose(); }
        catch (Exception error) { Diagnostic(entry).Capture(error, "DisposeBackend"); }
        entry.Registration.Definition.Metadata?.External?.Unload();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _entries.AsEnumerable().Reverse()) Stop(entry);
        Received = null;
        AccessChanged = null;
    }

    public async Task ShutdownAsync()
    {
        Dispose();
        await Task.WhenAll(_entries.Select(entry => entry.Cleanup));
        foreach (var entry in _entries)
        {
            entry.Cleanup = Task.CompletedTask;
            if (entry.Registration.Definition.Metadata?.External is { } external) await external.VerifyUnloadAsync();
        }
    }
}
