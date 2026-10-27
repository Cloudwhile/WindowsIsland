using System.Diagnostics;
using WindowsIsland.Contracts;
using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

internal sealed partial class PluginManager
{
    public static PluginManager CreateBuiltIns(BuiltInPluginContext context, SettingsStore settings) => new(
    [
        new(BuiltInPluginDefinitions.System, () => new SystemNotificationPlugin()),
        new(BuiltInPluginDefinitions.Telegram, () => new TelegramPlugin()),
        new(BuiltInPluginDefinitions.Power, () => new PowerPlugin())
    ], settings, action =>
    {
        if (context.Dispatcher.HasThreadAccess) action();
        else context.Dispatcher.TryEnqueue(() => action());
    }, id => id switch
    {
        BuiltInPluginIds.Power => new PowerBackend(context),
        BuiltInPluginIds.System => new SystemBackend(context),
        BuiltInPluginIds.Telegram => new TelegramBackend(context),
        BuiltInPluginIds.WeChat => new WeChatBackend(context),
        _ => null
    });

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

    private sealed class TelegramBackend(BuiltInPluginContext context) : SourceBackend(BuiltInPluginIds.Telegram)
    {
        private TelegramNativeService? _native;
        private MessengerHookService? _popups;
        private int _ticks;
        public override PluginStatus Status => _popups?.ConnectedApps.Contains("telegram") == true
            ? new(PluginConnectionState.Connected) : new(PluginConnectionState.NotRunning, PluginStatusReason.ClientNotDetected);
        public override Task StartAsync()
        {
            if (IsDisposed || _native is not null) return Task.CompletedTask;
            _native = new TelegramNativeService(context.Dispatcher, context.Verification);
            _native.Received += Publish;
            _popups = new MessengerHookService(context.Dispatcher, context.FixtureDirectory("WindowsIsland.HookFixture"),
                identity => !IsDisposed && identity == "telegram");
            _popups.NativeTelegramConnected = _native.IsConnected;
            _popups.Received += Publish;
            _popups.Start();
            _native.RefreshClients(Clients());
            return Task.CompletedTask;
        }
        public override Task PollAsync()
        {
            if (!IsDisposed && ++_ticks % 5 == 0)
            {
                _popups?.RefreshClients();
                _native?.RefreshClients(Clients());
            }
            return Task.CompletedTask;
        }
        private int[] Clients()
        {
            if (!context.Verification) return _popups?.TelegramProcessIds ?? [];
            var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "native", "Release", "WindowsIsland.TelegramFixture.exe"));
            var clients = new List<int>();
            foreach (var process in Process.GetProcessesByName("WindowsIsland.TelegramFixture"))
            {
                using (process)
                {
                    try { if (string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) clients.Add(process.Id); }
                    catch (Exception) { }
                }
            }
            return clients.ToArray();
        }
        protected override void DisposeCore()
        {
            try { if (_popups is not null) { _popups.Received -= Publish; _popups.Dispose(); } }
            finally { if (_native is not null) { _native.Received -= Publish; _native.Dispose(); } }
        }
    }

    private sealed class WeChatBackend(BuiltInPluginContext context) : SourceBackend(BuiltInPluginIds.WeChat), IPluginRetry
    {
        private WeChatService? _reader;
        private MessengerHookService? _popups;
        private int _ticks;
        public override PluginStatus Status => PluginNotificationBridge.FromKey(_reader?.StatusKey ?? "NotConnected");
        public bool CanRetry => _reader?.StatusKey is "WeChatUnsupported" or "WeChatConnectFailed";
        public void Retry() { if (!IsDisposed) _reader?.Retry(); }
        public override Task StartAsync()
        {
            if (IsDisposed || _reader is not null) return Task.CompletedTask;
            _reader = new WeChatService(() => !IsDisposed,
                action => context.Dispatcher.TryEnqueue(() => { if (!IsDisposed) action(); }),
                context.FixtureDirectory("WindowsIsland.WeChatTests"));
            _reader.Received += Publish;
            _popups = new MessengerHookService(context.Dispatcher, context.FixtureDirectory("WindowsIsland.HookFixture"),
                identity => !IsDisposed && identity == "wechat");
            _popups.Received += Publish;
            _popups.Start();
            _reader.Start();
            return Task.CompletedTask;
        }
        public override Task PollAsync()
        {
            if (!IsDisposed && ++_ticks % 5 == 0) _popups?.RefreshClients();
            return Task.CompletedTask;
        }
        public override bool Allows(PluginNotification notification) => !IsDisposed
            && _reader?.AllowsConversation(notification.ConversationName, notification.OriginProcessId) == true;
        protected override void DisposeCore()
        {
            try { if (_popups is not null) { _popups.Received -= Publish; _popups.Dispose(); } }
            finally { if (_reader is not null) { _reader.Received -= Publish; _reader.Dispose(); } }
        }
    }
}
