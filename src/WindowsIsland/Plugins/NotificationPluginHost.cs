using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

// Invoke host operations on the UI thread. Async lifecycle operations are
// serialized; settings take effect at the event boundary immediately.
internal sealed class NotificationPluginHost : IDisposable
{
    private sealed class Entry(NotificationPluginRegistration registration)
    {
        public readonly NotificationPluginRegistration Registration = registration;
        public INotificationPlugin? Plugin;
        public Action<IslandNotification>? Received;
        public Action<NotificationAccess>? AccessChanged;
        public bool Active, Failed;
    }

    private readonly Entry[] _entries;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private AppSettings _settings;
    private bool _started, _disposed;
    public NotificationAccess Access { get; private set; } = NotificationAccess.Waiting;
    public event Action<IslandNotification>? Received;
    public event Action<NotificationAccess>? AccessChanged;

    public NotificationPluginHost(IEnumerable<NotificationPluginRegistration> registrations, AppSettings settings)
    {
        var items = registrations.ToArray();
        if (items.Any(item => string.IsNullOrWhiteSpace(item.Definition.Id))
            || items.Select(item => item.Definition.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Length)
            throw new ArgumentException("Plugin identifiers must be nonempty and unique.", nameof(registrations));
        _entries = items.Select(item => new Entry(item)).ToArray();
        _settings = settings;
    }

    public Task StartAsync()
    {
        if (_disposed) return Task.CompletedTask;
        _started = true;
        return ReconcileAsync();
    }

    public Task ApplySettingsAsync(AppSettings settings)
    {
        if (_disposed) return Task.CompletedTask;
        var previous = _settings;
        _settings = settings;
        // A disable invalidates in-flight callbacks even if re-enabled before
        // the asynchronous start or poll finishes.
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
                if (!entry.Registration.Definition.IsEnabled(_settings)) { Stop(entry); entry.Failed = false; continue; }
                if (entry.Plugin is not null || entry.Failed) continue;
                INotificationPlugin? starting = null;
                try
                {
                    var plugin = entry.Registration.Create();
                    starting = plugin;
                    entry.Plugin = plugin;
                    entry.Received = notification =>
                    {
                        if (!_disposed && entry.Active && ReferenceEquals(entry.Plugin, plugin)
                            && entry.Registration.Definition.IsEnabled(_settings)) Received?.Invoke(notification);
                    };
                    plugin.Received += entry.Received;
                    if (plugin is INotificationAccessPlugin access)
                    {
                        entry.AccessChanged = value => { if (!_disposed && ReferenceEquals(entry.Plugin, plugin)) SetAccess(value); };
                        access.AccessChanged += entry.AccessChanged;
                    }
                    await plugin.StartAsync();
                    if (_disposed || !ReferenceEquals(entry.Plugin, plugin)) continue;
                    entry.Active = true;
                    if (plugin is INotificationAccessPlugin initialized) SetAccess(initialized.Access);
                }
                catch (Exception error)
                {
                    if (!_disposed && (starting is null || ReferenceEquals(entry.Plugin, starting))) Fail(entry, error, "Start");
                }
            }
        }
        finally { _operations.Release(); }
    }

    public async Task PollAsync()
    {
        if (_disposed || !_started || !await _operations.WaitAsync(0)) return;
        try
        {
            foreach (var entry in _entries)
            {
                if (_disposed) return;
                if (!entry.Active || entry.Plugin is not { } plugin) continue;
                try { await plugin.PollAsync(); }
                catch (Exception error) { if (ReferenceEquals(entry.Plugin, plugin)) Fail(entry, error, "Poll"); }
            }
        }
        finally { _operations.Release(); }
    }

    public async Task RequestAccessAsync()
    {
        await ReconcileAsync();
        await _operations.WaitAsync();
        try
        {
            if (_disposed) return;
            foreach (var entry in _entries)
                if (entry.Plugin is INotificationAccessPlugin access)
                {
                    try { await access.RequestAccessAsync(); }
                    catch (Exception error) { Fail(entry, error, "RequestAccess"); }
                }
        }
        finally { _operations.Release(); }
    }

    public IReadOnlyList<NotificationPluginStatus> Snapshot() => _entries.Select(entry =>
    {
        var key = "Off";
        var retry = false;
        if (entry.Registration.Definition.IsEnabled(_settings))
        {
            try
            {
                key = entry.Failed ? "Unavailable" : entry.Plugin?.StatusKey ?? "NotConnected";
                retry = entry.Failed || entry.Plugin is INotificationRetryPlugin { CanRetry: true };
            }
            catch (Exception error) { Fail(entry, error, "Status"); key = "Unavailable"; retry = true; }
        }
        return new NotificationPluginStatus(entry.Registration.Definition, key, retry);
    }).ToArray();

    // A failed plugin is recreated; a running plugin retries its own connection.
    public Task RetryAsync(string id)
    {
        if (_disposed) return Task.CompletedTask;
        var entry = _entries.FirstOrDefault(item => item.Registration.Definition.Id == id);
        if (entry is null || !entry.Registration.Definition.IsEnabled(_settings)) return Task.CompletedTask;
        if (entry.Failed)
        {
            entry.Failed = false;
            return _started ? ReconcileAsync() : Task.CompletedTask;
        }
        if (entry.Plugin is INotificationRetryPlugin plugin)
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
            try { if (!entry.Plugin.Allows(notification)) return false; }
            catch (Exception error) { Fail(entry, error, "Filter"); return false; }
        }
        return true;
    }

    private void Fail(Entry entry, Exception error, string operation)
    {
        Diagnostics.Capture(entry.Registration.Definition.DiagnosticArea, error, "Plugin." + entry.Registration.Definition.Id + "." + operation);
        var access = entry.Plugin is INotificationAccessPlugin;
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

    private static void Stop(Entry entry)
    {
        var plugin = entry.Plugin;
        entry.Active = false;
        entry.Plugin = null;
        if (plugin is null) return;
        try
        {
            plugin.Received -= entry.Received;
            if (plugin is INotificationAccessPlugin access) access.AccessChanged -= entry.AccessChanged;
            plugin.Dispose();
        }
        catch (Exception error)
        {
            Diagnostics.Capture(entry.Registration.Definition.DiagnosticArea, error, "Plugin." + entry.Registration.Definition.Id + ".Dispose");
        }
        finally { entry.Received = null; entry.AccessChanged = null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _entries.Reverse()) Stop(entry);
        Received = null;
        AccessChanged = null;
        // An outstanding awaited operation still owns the semaphore.
    }
}
