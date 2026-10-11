using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

internal sealed class SystemNotificationPlugin(BuiltInPluginContext context) : NotificationPlugin, INotificationAccessPlugin
{
    private NotificationService? _service;
    public NotificationAccess Access => _service?.Access ?? NotificationAccess.Waiting;
    public event Action<NotificationAccess>? AccessChanged;
    public override string StatusKey => Access == NotificationAccess.Allowed ? "On" : "AwaitingPermission";

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
    private void OnAccessChanged(NotificationAccess access) { if (!IsDisposed) AccessChanged?.Invoke(access); }
    protected override void DisposeCore()
    {
        AccessChanged = null;
        if (_service is null) return;
        _service.Received -= Publish;
        _service.AccessChanged -= OnAccessChanged;
        _service.Dispose();
    }
}
