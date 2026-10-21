using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.LogicTests;

internal static class PluginCatalogChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var builtIns = new[] { BuiltInPluginDefinitions.System, BuiltInPluginDefinitions.WeChat,
            BuiltInPluginDefinitions.Telegram, BuiltInPluginDefinitions.Power };
        check(builtIns.All(d => d.Metadata is { IsBuiltIn: true, Author: "WINI" } metadata
            && metadata.Version == ReleaseVersion.Current.Tag && File.Exists(metadata.Location) && !d.CanRemove),
            "Built-in metadata reports the actual host version and shared assembly location");
        check(await PluginMetadata.BuiltIn.ReadSizeAsync() == new FileInfo(PluginMetadata.BuiltIn.Location).Length,
            "Plugin size is read from the actual assembly file");
        check(await new PluginMetadata(false, "1.2.3", Path.Combine(root, "missing.dll")).ReadSizeAsync() is null,
            "A missing plugin assembly leaves its size unknown");
        foreach (var definition in builtIns)
        {
            using var manager = new PluginManager([new(definition with { Metadata = definition.Metadata! with { IsBuiltIn = false } },
                () => new Source(definition.Id))], new SettingsStore(Path.Combine(root, definition.Id + ".json")));
            var blocked = false;
            try { await manager.RemoveAsync(definition.Id); }
            catch (InvalidOperationException) { blocked = true; }
            check(blocked && manager.Snapshot().Count == 1, "Reserved built-in identity cannot be removed: " + definition.Id);
        }

        var id = "wini_example_plugin";
        var definitionExternal = BuiltInPluginDefinitions.WeChat with
        { Id = id, Metadata = new(false, "1.2.3", Path.Combine(root, "example.dll"), "Example") };
        var path = Path.Combine(root, "removed-plugin.json");
        var store = new SettingsStore(path);
        store.SetPluginString(id, "mode", "只删除本插件");
        store.SetPluginString(BuiltInPluginIds.Power, "mode", "保留");
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var external = new Source(id) { Stopping = stopped.Task };
        var power = new Source(BuiltInPluginIds.Power);
        using var catalog = new PluginManager([new(definitionExternal, () => external),
            new(BuiltInPluginDefinitions.Power, () => power)], store);
        var arrivals = 0;
        catalog.Received += _ => arrivals++;
        await catalog.StartAsync();
        var oldContext = external.Context!;
        oldContext.PublishNotification(new(id, "会话", "发送者", "正文", DateTimeOffset.UtcNow));
        var removal = catalog.RemoveAsync(id);
        oldContext.PublishNotification(new(id, "会话", "发送者", "迟到消息", DateTimeOffset.UtcNow));
        check(!removal.IsCompleted && external.Calls.SequenceEqual(["Initialize", "Start", "Stop"])
            && catalog.Snapshot().Single().Definition.Id == BuiltInPluginIds.Power && arrivals == 1,
            "Removal immediately hides the selected plugin and revokes callbacks while awaiting Stop");
        stopped.SetResult();
        await removal;
        await catalog.RemoveAsync(id);
        oldContext.Settings.SetString("mode", "迟到写入");
        check(external.Calls.SequenceEqual(["Initialize", "Start", "Stop", "Dispose"])
            && power.Calls.SequenceEqual(["Initialize", "Start"]),
            "Removal waits for Stop before Dispose, is idempotent and leaves other plugins running");
        var reenableBlocked = false;
        try { await catalog.SetEnabledAsync(id, true); }
        catch (InvalidOperationException) { reenableBlocked = true; }
        check(reenableBlocked, "A removed registration cannot be re-enabled");
        store.Save(store.Current with { Animations = false });
        var restarted = new SettingsStore(path);
        var created = false;
        using var reopened = new PluginManager([new(definitionExternal, () => { created = true; return new Source(id); }),
            new(BuiltInPluginDefinitions.Power, () => new Source(BuiltInPluginIds.Power))], restarted);
        await reopened.StartAsync();
        check(!created && reopened.Snapshot().Single().Definition.Id == BuiltInPluginIds.Power && !restarted.Current.Animations
            && restarted.GetPluginString(id, "mode", null) is null
            && restarted.GetPluginString(BuiltInPluginIds.Power, "mode", null) == "保留",
            "Removed plugins stay absent after restart while other settings and plugin namespaces survive");

        var corrupt = Path.Combine(root, "removed-corrupt.json");
        File.WriteAllText(corrupt, """
            {"WeChat":false,"Animations":false,"RemovedPlugins":[17,null,"wrong","wini_example_plugin","wini_power_plugin"]}
            """);
        var damaged = new SettingsStore(corrupt);
        using var damagedManager = new PluginManager([new(definitionExternal, () => new Source(id)),
            new(BuiltInPluginDefinitions.Power, () => new Source(BuiltInPluginIds.Power))], damaged);
        check(damagedManager.Snapshot().Single().Definition.Id == BuiltInPluginIds.Power && !damaged.Current.WeChat
            && !damaged.Current.Animations && damaged.IsPluginRemoved(id),
            "Damaged removal entries are ignored individually and cannot hide a built-in plugin");

        var failedPath = Path.Combine(root, "removed-save-failure.json");
        var failedStore = new SettingsStore(failedPath);
        failedStore.SetPluginString(id, "mode", "保留");
        var alive = new Source(id);
        using var failing = new PluginManager([new(definitionExternal, () => alive)], failedStore);
        await failing.StartAsync();
        File.Delete(failedPath);
        Directory.CreateDirectory(failedPath);
        try
        {
            var rejected = false;
            try { await failing.RemoveAsync(id); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = true; }
            check(rejected && !failedStore.IsPluginRemoved(id) && failedStore.GetPluginString(id, "mode", null) == "保留"
                && failing.Snapshot().Count == 1 && alive.Calls.SequenceEqual(["Initialize", "Start"]),
                "A failed removal write retains the running instance, plugin settings and list entry");
        }
        finally { Directory.Delete(failedPath); }
    }

    private sealed class Source(string id) : IMessageSource
    {
        public string Id => id;
        public string DisplayName => id;
        public IPluginContext? Context;
        public Task Stopping = Task.CompletedTask;
        public readonly List<string> Calls = [];
        public event Action<PluginNotification>? NotificationReceived { add { } remove { } }
        public Task InitializeAsync(IPluginContext context) { Context = context; Calls.Add("Initialize"); return Task.CompletedTask; }
        public Task StartAsync() { Calls.Add("Start"); return Task.CompletedTask; }
        public Task StopAsync() { Calls.Add("Stop"); return Stopping; }
        public void Dispose() => Calls.Add("Dispose");
    }
}
