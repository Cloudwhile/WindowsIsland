using System.Reflection;
using WindowsIsland.Contracts;
using WindowsIsland.PluginIsolation;
using WindowsIsland.Plugins;

internal static class PluginConversationSnapshotChecks
{
    public static void Run(Action<bool, string> check)
    {
        var paths = new PluginPaths(Path.Combine(Path.GetTempPath(), "wini-policy-" + Guid.NewGuid().ToString("N")));
        var manifest = new PluginManifest("policy_fixture", "Policy fixture", "1.0.0", "fixture.dll", "Fixture", 1, []);
        using var proxy = new SandboxPluginProxy(new(paths, manifest, paths.Root));
        var type = typeof(SandboxPluginProxy);
        type.GetField("_desktop", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(proxy, new DesktopMarker());
        var apply = type.GetMethod("ApplyDescription", BindingFlags.NonPublic | BindingFlags.Instance)!;
        void Snapshot(long revision, params PluginConversationRule[] rules) => apply.Invoke(proxy,
            [new WorkerDescription(new(PluginConnectionState.Connected), [], rules, revision)]);
        var message = new PluginNotification(manifest.Id, "首次会话", null, "测试消息", DateTimeOffset.UtcNow,
            OriginProcessId: 42, AppId: "wechat", Kind: PluginNotificationKind.ClientAutomation);
        Snapshot(1);
        check(!proxy.Allows(message), "An unknown conversation remains blocked until its policy is observed");
        Snapshot(3, new PluginConversationRule("wechat", 42, "首次会话", false));
        check(proxy.Allows(message), "The current unmuted policy admits the first conversation message immediately");
        Snapshot(2);
        check(proxy.Allows(message), "An older scheduled poll cannot remove a newer callback conversation policy");
        Snapshot(4, new PluginConversationRule("wechat", 42, "首次会话", true));
        Snapshot(3, new PluginConversationRule("wechat", 42, "首次会话", false));
        check(!proxy.Allows(message), "A delayed unmuted snapshot cannot reverse a newer mute decision");
        Snapshot(4, new PluginConversationRule("wechat", 42, "首次会话", false));
        check(!proxy.Allows(message), "Repeated snapshot revisions cannot replace an already accepted mute policy");
        Snapshot(5, new PluginConversationRule("wechat", 43, "首次会话", false));
        check(!proxy.Allows(message), "A policy for another client process cannot authorize this message");
        Snapshot(6, new PluginConversationRule("wechat", 42, "首次会话", false));
        proxy.Revoke();
        check(!proxy.Allows(message), "Revocation invalidates a previously accepted notification policy immediately");
    }

    private sealed class DesktopMarker : IPluginDesktop
    {
        public Task<DesktopClient[]> FindClientsAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<DesktopTree> ReadTreeAsync(int processId, long window, CancellationToken token = default) => throw new NotSupportedException();
        public Task<byte[]> ReadModuleAsync(DesktopRead request, CancellationToken token = default) => throw new NotSupportedException();
        public Task<byte[]> ReadMemoryAsync(DesktopRead request, CancellationToken token = default) => throw new NotSupportedException();
        public Task<bool> SetFlagAsync(DesktopFlag request, CancellationToken token = default) => throw new NotSupportedException();
        public Task<byte[]?> ReadImageAsync(DesktopImage request, CancellationToken token = default) => throw new NotSupportedException();
    }
}
