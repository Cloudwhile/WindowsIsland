using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

// 旧适配器仅作为通用生命周期与去重的回归夹具，产品入口位于独立插件仓库。
internal sealed class WeChatPlugin() : BuiltInMessageSource(BuiltInPluginIds.WeChat, "WeChat"), IPluginRetry
{
    private readonly string _session = Guid.NewGuid().ToString("N");
    public bool CanRetry => Backend is IPluginRetry { CanRetry: true };
    public void Retry() { if (Backend is IPluginRetry retry) retry.Retry(); }

    // 既有监听器重建后序号归零；用实例标识区分新事件，补全仍沿用同一个键。
    protected override void Publish(PluginNotification notification) => base.Publish(
        notification is { Kind: PluginNotificationKind.ClientAutomation, DeduplicationKey: { } key }
            ? notification with { DeduplicationKey = _session + "/" + key } : notification);
}
