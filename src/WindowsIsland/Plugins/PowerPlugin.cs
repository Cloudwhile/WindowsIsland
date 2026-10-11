using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

internal sealed class PowerPlugin(BuiltInPluginContext context) : NotificationPlugin
{
    private PowerService? _service;
    public override string StatusKey => "On";
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
