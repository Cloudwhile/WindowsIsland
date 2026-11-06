using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

internal static class IslandSchedulerChecks
{
    private static readonly IslandNotification Message = new(1, DateTimeOffset.UnixEpoch, "Mail", "标题", "正文");
    private static readonly PinnedContent Pin = new("sample", PinnedSourceFixture.PluginId, "固定内容", "0");

    public static async Task RunAsync(Action<bool, string> check)
    {
        // 覆盖三态与全部六种事件，Expanded 同时覆盖有无固定内容。
        foreach (var initial in new[] { "minimal", "compact", "expanded", "expanded+pinned" })
        foreach (var operation in new[] { "add", "remove", "update", "message", "dismiss", "expire" })
        {
            var time = new Clock();
            var scheduler = new IslandScheduler(time);
            var pinned = initial is "compact" or "expanded+pinned";
            var message = initial is "expanded" or "expanded+pinned";
            if (pinned) scheduler.PinnedAdded(Pin);
            if (message) scheduler.MessageArrived(Message);
            switch (operation)
            {
                case "add": scheduler.PinnedAdded(Pin); pinned = true; break;
                case "remove": scheduler.PinnedRemoved(Pin.PluginId, Pin.Id); pinned = false; break;
                case "update": scheduler.PinnedUpdated(Pin with { CompactText = "04:59" }); break;
                case "message": scheduler.MessageArrived(Message); message = true; break;
                case "dismiss": scheduler.MessageDismissed(); message = false; break;
                case "expire": time.Advance(5); scheduler.MessageExpired(); message = false; break;
            }
            var expected = message ? IslandState.Expanded : pinned ? IslandState.Compact : IslandState.Minimal;
            check(scheduler.State == expected && (scheduler.Pinned is not null) == pinned,
                $"Island transition {initial} + {operation} => {expected}");
        }
        var clock = new Clock();
        var island = new IslandScheduler(clock);
        island.PinnedAdded(Pin);
        island.MessageArrived(Message);
        clock.Advance(4);
        island.MessageUpdated(Message with { Body = "补全头像" });
        island.PinnedUpdated(Pin with { CompactText = "04:56" });
        clock.Advance(1);
        island.Tick();
        check(island.State == IslandState.Compact, "Metadata and pinned updates never renew the five second message deadline");
        island.MessageArrived(Message);
        clock.Advance(4);
        island.MessageArrived(Message with { Id = 2 });
        clock.Advance(1);
        island.MessageExpired();
        check(island.State == IslandState.Expanded && island.Message?.Id == 2, "Latest message replaces current and cancels the old expiry");
        clock.Advance(4);
        island.Tick();
        check(island.State == IslandState.Compact, "Replacement receives exactly five seconds before returning to compact");
        island.PinnedAdded(Pin with { Id = "new" });
        island.PinnedUpdated(Pin with { CompactText = "late" });
        island.PinnedRemoved(Pin.PluginId, Pin.Id);
        check(island.Pinned?.Id == "new", "Stale updates and removal cannot replace the latest pinned item");
        island.PinnedRemoved(Pin.PluginId);
        island.PinnedUpdated(Pin with { Id = "new" });
        check(island.State == IslandState.Minimal, "Pinned updates cannot resurrect removed content");
        island.PinnedAdded(Pin);
        island.MessageArrived(Message);
        island.SetMuted(true);
        clock.Advance(5);
        island.Tick();
        check(island.State == IslandState.Minimal && island.Pinned is not null && island.Message is null,
            "Mute discards messages and retains pinned content");
        island.SetMuted(false);
        check(island.State == IslandState.Compact, "Leaving mute restores compact without replaying messages");
        clock.Advance(365 * 86400);
        check(!island.Tick() && island.State == IslandState.Compact && island.Pinned == Pin,
            "A year of elapsed time cannot remove pinned content or report a timed transition");
        island.PinnedClicked();
        island.PinnedUpdated(Pin with { Body = "详情" });
        island.PinnedClicked();
        clock.Advance(365 * 86400);
        check(!island.Tick() && island.ExpandedKind == IslandExpandedKind.Pinned,
            "Elapsed time never closes expanded fixed details");
        foreach (var pinnedFirst in new[] { false, true })
        {
            var concurrent = new IslandScheduler(clock);
            if (pinnedFirst) concurrent.PinnedAdded(Pin);
            concurrent.MessageArrived(Message);
            if (!pinnedFirst) concurrent.PinnedAdded(Pin);
            check(concurrent.State == IslandState.Expanded && concurrent.Pinned == Pin, "Concurrent arrival ordering preserves message and pinned content: " + pinnedFirst);
            concurrent.PinnedRemoved(Pin.PluginId);
            check(concurrent.State == IslandState.Expanded, "Removing pinned content never dismisses a message: " + pinnedFirst);
            concurrent.MessageDismissed();
            check(concurrent.State == IslandState.Minimal, "Message returns to minimal after pinned removal: " + pinnedFirst);
        }
        var restored = JsonSerializer.Deserialize<PinnedContent>(JsonSerializer.Serialize(Pin));
        check(restored == Pin && restored!.Body is null && restored.ActionId is null,
            "Pinned contract round trips with optional fields and safe defaults");
        await CheckPinnedSource(check);
        await CheckIsolation(check);
    }

    private static SettingsStore Store() => new(Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "pinned", Guid.NewGuid().ToString("N"), "settings.json"));
    private static void Connect(PluginManager manager, IslandScheduler scheduler)
    {
        manager.PinnedChanged += change =>
        {
            switch (change.Kind)
            {
                case PinnedChangeKind.Added: scheduler.PinnedAdded(change.Content!); break;
                case PinnedChangeKind.Updated: scheduler.PinnedUpdated(change.Content!); break;
                case PinnedChangeKind.Removed: scheduler.PinnedRemoved(change.PluginId, change.Id); break;
            }
        };
        manager.Received += scheduler.MessageArrived;
    }

    private static async Task CheckPinnedSource(Action<bool, string> check)
    {
        var time = new Clock();
        var store = Store();
        store.Save(store.Current with { Animations = false });
        var scheduler = new IslandScheduler(time);
        PinnedSourceFixture? source = null;
        using var manager = new PluginManager([new(PinnedSourceFixture.Definition, () => source = new PinnedSourceFixture(time))], store);
        Connect(manager, scheduler);
        await manager.StartAsync();
        check(scheduler.State == IslandState.Compact && scheduler.Pinned?.CompactText == "0", "Test source submits through context during start and enters compact");
        for (var second = 1; second <= 12; second++)
        {
            time.Advance(1);
            await manager.PollAsync();
            if (second == 2) scheduler.MessageArrived(Message);
            scheduler.Tick();
            var expected = second is >= 2 and < 7 ? IslandState.Expanded : IslandState.Compact;
            check(scheduler.State == expected && scheduler.Pinned?.CompactText == second.ToString(),
                $"Fixed source end to end second {second}: updates survive message interruption and return");
        }
        time.Advance(365 * 86400);
        check(!scheduler.Tick() && scheduler.State == IslandState.Compact && scheduler.Pinned is not null,
            "The host retains a quiet fixed source after a year without polling or updates");
        source!.Remove();
        check(scheduler.State == IslandState.Minimal, "The source explicitly removes its fixed content through the context");
        await manager.RetryAsync(PinnedSourceFixture.PluginId);
        check(scheduler.State == IslandState.Compact && scheduler.Pinned?.CompactText == "0", "Retry starts a new fixed content lifecycle");
        scheduler.MessageArrived(Message);
        await manager.SetEnabledAsync(PinnedSourceFixture.PluginId, false);
        check(scheduler.State == IslandState.Expanded && scheduler.Pinned is null, "Disabling the source removes only pinned content");
        await manager.SetEnabledAsync(PinnedSourceFixture.PluginId, true);
        check(scheduler.Pinned is not null, "Re-enabling creates fresh fixed content");
        await manager.ShutdownAsync();
        check(scheduler.Pinned is null && scheduler.Message is not null, "Shutdown withdraws pinned content before cleanup");
        CheckRetiredPreferences(check);

    }

    private static void CheckRetiredPreferences(Action<bool, string> check)
    {
        foreach (var obsolete in new[] { "true", "false", "\"bad\"", "null", "{}" })
        {
            var path = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "pinned", Guid.NewGuid() + ".json");
            // 旧配置只作为兼容性输入，不恢复相应功能或迁移数据。
            File.WriteAllText(path, "{\"Countdown\":" + obsolete + ",\"Animations\":false,\"WeChat\":false,\"Plugins\":{\"retired\":{\"DurationSeconds\":\"60\"}}}");
            var store = new SettingsStore(path);
            var persisted = JsonSerializer.SerializeToElement(store.Current);
            check(!store.Current.Animations && !store.Current.WeChat && !persisted.TryGetProperty("Countdown", out _),
                "Unknown retired preferences are ignored without resetting valid settings: " + obsolete);
            store.Save(store.Current);
            check(new SettingsStore(path).Current == store.Current, "Retired preferences cannot reappear after save and restart");
        }
    }

    private static async Task CheckIsolation(Action<bool, string> check)
    {
        var store = Store();
        var scheduler = new IslandScheduler();
        Probe? source = null;
        using var manager = new PluginManager([new(PinnedSourceFixture.Definition, () => source = new Probe())], store);
        Connect(manager, scheduler);
        await manager.StartAsync();
        check(scheduler.Pinned is not null, "Pinned source capability is available without exposing UI");
        var oldContext = source!.Context!;
        var oldCapability = oldContext.PinnedContent!;
        source.ThrowPoll = true;
        scheduler.MessageArrived(Message);
        await manager.PollAsync();
        check(scheduler.State == IslandState.Expanded && scheduler.Pinned is null, "Plugin crash immediately withdraws content and preserves current message");
        oldCapability.Submit(Pin);
        check(scheduler.Pinned is null && oldContext.PinnedContent is null, "Revoked capabilities and old callbacks cannot restore pinned content");
        await manager.RetryAsync(PinnedSourceFixture.PluginId);
        oldCapability.Update(Pin);
        check(scheduler.Pinned == Pin && source!.Context != oldContext, "Retry binds a fresh generation and rejects old updates");
        source!.Context!.PublishNotification(new(PinnedSourceFixture.PluginId, "标题", null, "正文", DateTimeOffset.UtcNow, AppName: "Mail"));
        check(scheduler.Message?.AppName == "Mail", "Message delivery remains operational after pinned plugin failure and retry");

        Probe? broken = null;
        using var failing = new PluginManager([new(PinnedSourceFixture.Definition, () => broken = new Probe { ThrowStart = true })], store);
        var changes = new List<PinnedContentChange>();
        failing.PinnedChanged += changes.Add;
        await failing.StartAsync();
        check(changes.All(c => c.Kind == PinnedChangeKind.Removed) && failing.Snapshot().Single().StatusKey == "Unavailable",
            "Failed startup never publishes its pending pinned item");

        var queue = new Queue<Action>();
        using var queued = new PluginManager([new(PinnedSourceFixture.Definition, () => new Probe())], store, queue.Enqueue);
        var queuedScheduler = new IslandScheduler();
        Connect(queued, queuedScheduler);
        await queued.StartAsync();
        await queued.SetEnabledAsync(PinnedSourceFixture.PluginId, false);
        while (queue.TryDequeue(out var action)) action();
        check(queuedScheduler.Pinned is null, "Queued background submission after stop is rejected by instance generation");

        var removable = PinnedSourceFixture.Definition with { Metadata = new(false, "1.0.0", "fixture.dll") };
        using var uninstalling = new PluginManager([new(removable, () => new Probe())], store);
        var removalScheduler = new IslandScheduler();
        Connect(uninstalling, removalScheduler);
        await uninstalling.StartAsync();
        removalScheduler.PinnedClicked();
        removalScheduler.MessageArrived(Message);
        await uninstalling.RemoveAsync(removable.Id);
        check(removalScheduler.State == IslandState.Expanded && removalScheduler.Pinned is null
            && uninstalling.Snapshot().Count == 0, "Uninstall withdraws fixed content without interrupting an active message");
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public void Advance(double seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
    private sealed class Probe : IMessageSource, IPluginPolling
    {
        public IPluginContext? Context;
        public bool ThrowPoll, ThrowStart;
        public string Id => PinnedSourceFixture.PluginId;
        public string DisplayName => "固定内容夹具";
        public event Action<PluginNotification>? NotificationReceived { add { } remove { } }
        public Task InitializeAsync(IPluginContext context) { Context = context; return Task.CompletedTask; }
        public Task StartAsync() { Context!.PinnedContent!.Submit(Pin); if (ThrowStart) throw new InvalidOperationException(); return Task.CompletedTask; }
        public Task PollAsync() { if (ThrowPoll) throw new InvalidOperationException(); return Task.CompletedTask; }
        public Task StopAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
}
