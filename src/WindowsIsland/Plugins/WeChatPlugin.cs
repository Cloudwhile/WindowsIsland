using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

internal sealed class WeChatPlugin(BuiltInPluginContext context) : NotificationPlugin, INotificationRetryPlugin
{
    private WeChatService? _reader;
    private MessengerHookService? _popups;
    private int _ticks;
    public override string StatusKey => _reader?.StatusKey ?? "NotConnected";
    public bool CanRetry => StatusKey is "WeChatUnsupported" or "WeChatConnectFailed";
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

    // Applies to every source, including Windows toasts and popup fallback.
    public override bool Allows(IslandNotification notification) => !IsDisposed
        && _reader?.AllowsConversation(notification.Title, notification.OriginProcessId) == true;

    protected override void DisposeCore()
    {
        try { if (_popups is not null) { _popups.Received -= Publish; _popups.Dispose(); } }
        finally { if (_reader is not null) { _reader.Received -= Publish; _reader.Dispose(); } }
    }
}
