using Microsoft.UI.Dispatching;
using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

internal sealed record BuiltInPluginContext(DispatcherQueue Dispatcher, nint Window, bool Verification)
{
    public string? FixtureDirectory(string project) => Verification
        ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "bin", project)) : null;
}

internal static class BuiltInPlugins
{
    public static NotificationPluginHost Create(BuiltInPluginContext context, AppSettings settings) => new(
    [
        new(BuiltInPluginDefinitions.System, () => new SystemNotificationPlugin(context)),
        new(BuiltInPluginDefinitions.WeChat, () => new WeChatPlugin(context)),
        new(BuiltInPluginDefinitions.Telegram, () => new TelegramPlugin(context)),
        new(BuiltInPluginDefinitions.Power, () => new PowerPlugin(context))
    ], settings);
}

internal abstract class NotificationPlugin : INotificationPlugin
{
    protected bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    private int _disposed;
    public event Action<IslandNotification>? Received;
    public abstract string StatusKey { get; }
    public abstract Task StartAsync();
    public virtual Task PollAsync() => Task.CompletedTask;
    public virtual bool Allows(IslandNotification notification) => !IsDisposed;
    protected void Publish(IslandNotification notification) { if (!IsDisposed) Received?.Invoke(notification); }
    protected abstract void DisposeCore();
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { DisposeCore(); }
        finally { Received = null; }
    }
}
