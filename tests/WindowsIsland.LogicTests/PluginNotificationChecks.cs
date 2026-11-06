using WindowsIsland.Plugins;
using WindowsIsland.Services;
using static WindowsIsland.LogicTests.PluginDirectoryChecks;

namespace WindowsIsland.LogicTests;

internal static class PluginNotificationChecks
{
    private static void Check(bool value, string message) => PluginDirectoryChecks.Check(value, message);

    public static async Task RunAsync()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "plugin-notifications", Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(root, "settings.json"));
        store.Save(store.Current with { SystemNotifications = false, Power = false, Telegram = false, WeChat = false });
        var paths = new PluginPaths(Path.Combine(root, "plugins"));
        WritePlugin(paths.Code(WindowsIsland.Contracts.PluginSourceKind.Manual, Manifest.Id), Manifest);
        var builtIns = new[] { BuiltInPluginDefinitions.System, BuiltInPluginDefinitions.Power }
            .Select(definition => new NotificationPluginRegistration(definition, () => throw new InvalidOperationException("Disabled built-in was created.")));
        using var manager = new PluginManager(builtIns, store);
        manager.ConfigureDirectories(paths);
        manager.ConfirmEnableAsync = _ => Task.FromResult(true);
        var received = new System.Collections.Concurrent.ConcurrentQueue<IslandNotification>();
        manager.Received += received.Enqueue;
        await manager.StartAsync();
        await manager.SetEnabledAsync(Manifest.Id, true);
        async Task<IslandNotification> NextAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            do
            {
                if (received.TryDequeue(out var value)) return value;
                await Task.Delay(25);
            } while (DateTime.UtcNow < deadline);
            throw new InvalidOperationException("External LPAC notification did not arrive.");
        }
        var first = await NextAsync();
        Check(first.PluginOwner?.Id == Manifest.Id && manager.Allows(first),
            "External LPAC notifications retain host ownership and ignore disabled built-in power switches");
        var generic = first with { Source = NotificationSource.SystemNotification };
        Check(manager.Allows(generic) && !BuiltInPluginDefinitions.System.Matches(generic),
            "Generic external notifications use their own switch instead of the disabled system notification source");
        Check(!manager.Allows(generic with { PluginOwner = null }) && !manager.Allows(first with { PluginOwner = null }),
            "Unowned system and power notifications still obey their built-in switches");
        Check(!manager.Allows(generic with { AppId = "Telegram" }),
            "Host ownership preserves cross-source messenger preferences");
        await manager.SetEnabledAsync(Manifest.Id, false);
        Check(!manager.Allows(first) && !manager.Allows(generic), "Disabling rejects queued notifications from the previous plugin instance");
        await manager.SetEnabledAsync(Manifest.Id, true);
        var second = await NextAsync();
        Check(manager.Allows(second) && !manager.Allows(first), "Re-enabling accepts the new LPAC instance and never revives old queued notifications");
        Check(!manager.Allows(second with { PluginOwner = second.PluginOwner! with { Id = "wini_plugin_unknown" } }),
            "Unknown publishing identities fail closed");
        await manager.ShutdownAsync();
        Check(!manager.Allows(second), "Shutdown rejects retained plugin notification ownership");
    }
}
