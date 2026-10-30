using WindowsIsland.Contracts;
using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

internal sealed partial class PluginManager
{
    public static PluginManager CreateBuiltIns(BuiltInPluginContext context, SettingsStore settings) => new(
    [
        new(BuiltInPluginDefinitions.System, () => new SystemNotificationPlugin()),
        new(BuiltInPluginDefinitions.Power, () => new PowerPlugin())
    ], settings, action =>
    {
        if (context.Dispatcher.HasThreadAccess) action();
        else context.Dispatcher.TryEnqueue(() => action());
    }, id => id switch
    {
        BuiltInPluginIds.Power => new PowerBackend(context),
        BuiltInPluginIds.System => new SystemBackend(context),
        _ => null
    }, external => new PluginDesktopBroker(external.Manifest, external.HostRoot, external.Directory));

    private abstract class SourceBackend(string id) : IMessageSourceBackend
    {
        private int _disposed;
        protected bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public event Action<PluginNotification>? NotificationReceived;
        public abstract PluginStatus Status { get; }
        public abstract Task StartAsync();
        public virtual Task PollAsync() => Task.CompletedTask;
        public virtual bool Allows(PluginNotification notification) => !IsDisposed;
        protected void Publish(IslandNotification notification)
        {
            if (!IsDisposed) NotificationReceived?.Invoke(PluginNotificationBridge.ToContract(id, notification));
        }
        protected abstract void DisposeCore();
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { DisposeCore(); }
            finally { NotificationReceived = null; }
        }
    }

    private sealed class PowerBackend(BuiltInPluginContext context) : SourceBackend(BuiltInPluginIds.Power)
    {
        private PowerService? _service;
        public override PluginStatus Status => new(PluginConnectionState.Connected);
        public override Task StartAsync()
        {
            if (!IsDisposed && _service is null)
            {
                _service = new PowerService(context.Window, context.Dispatcher);
                _service.Received += Publish;
            }
            return Task.CompletedTask;
        }
        public override Task PollAsync()
        {
            if (!IsDisposed) _service?.Poll();
            return Task.CompletedTask;
        }
        protected override void DisposeCore()
        {
            if (_service is null) return;
            _service.Received -= Publish;
            _service.Dispose();
        }
    }

    private sealed class SystemBackend(BuiltInPluginContext context) : SourceBackend(BuiltInPluginIds.System), IPluginNotificationAccess
    {
        private NotificationService? _service;
        public PluginNotificationAccess Access => (PluginNotificationAccess)(_service?.Access ?? NotificationAccess.Waiting);
        public event Action<PluginNotificationAccess>? AccessChanged;
        public override PluginStatus Status => Access switch
        {
            PluginNotificationAccess.Allowed => new(PluginConnectionState.Connected),
            PluginNotificationAccess.NeedsRegistration => new(PluginConnectionState.Disconnected, PluginStatusReason.RegistrationRequired),
            PluginNotificationAccess.Denied => new(PluginConnectionState.Disconnected, PluginStatusReason.PermissionDenied),
            PluginNotificationAccess.Unavailable => new(PluginConnectionState.Disconnected, PluginStatusReason.Unavailable),
            _ => new(PluginConnectionState.Disconnected, PluginStatusReason.PermissionRequired)
        };

        public override async Task StartAsync()
        {
            if (IsDisposed || _service is not null) return;
            _service = new NotificationService(context.Dispatcher);
            _service.Received += Publish;
            _service.AccessChanged += OnAccessChanged;
            await _service.InitializeAsync(requestAccess: context.Verification);
        }
        public override Task PollAsync() => IsDisposed ? Task.CompletedTask : _service?.PollAsync() ?? Task.CompletedTask;
        public Task RequestAccessAsync() => IsDisposed ? Task.CompletedTask : _service?.InitializeAsync() ?? Task.CompletedTask;
        private void OnAccessChanged(NotificationAccess access) { if (!IsDisposed) AccessChanged?.Invoke((PluginNotificationAccess)access); }
        protected override void DisposeCore()
        {
            AccessChanged = null;
            if (_service is null) return;
            _service.Received -= Publish;
            _service.AccessChanged -= OnAccessChanged;
            _service.Dispose();
        }
    }

}
