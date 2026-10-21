using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

internal static class IslandSchedulerChecks
{
    private static readonly IslandNotification Message = new(1, DateTimeOffset.UnixEpoch, "Mail", "标题", "正文");
    private static readonly PinnedContent Pin = new("timer", BuiltInPluginIds.Countdown, "倒计时", "05:00");

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
        island.PinnedAdded(Pin with { ExpiresAt = clock.GetUtcNow().AddSeconds(6) });
        island.MessageArrived(Message);
        island.SetMuted(true);
        clock.Advance(5);
        island.Tick();
        check(island.State == IslandState.Minimal && island.Pinned is not null && island.Message is null,
            "Mute discards messages and retains the independently running pinned deadline");
        island.SetMuted(false);
        check(island.State == IslandState.Compact, "Leaving mute restores compact without replaying messages");
        clock.Advance(1);
        island.Tick();
        check(island.State == IslandState.Minimal, "Pinned absolute deadline expires even across mute");
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
        check(restored == Pin && restored!.Body is null && restored.ExpiresAt is null && restored.ActionId is null,
            "Pinned contract round trips with optional fields and safe defaults");
        await CheckCountdown(check);
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

    private static async Task CheckCountdown(Action<bool, string> check)
    {
        var time = new Clock();
        var store = Store();
        check(!store.Current.Countdown, "Countdown is disabled for fresh preferences");
        store.Save(store.Current with { Countdown = true, Animations = false });
        store.SetPluginString(BuiltInPluginIds.Countdown, "DurationSeconds", "12");
        var scheduler = new IslandScheduler(time);
        using var manager = new PluginManager([new(BuiltInPluginDefinitions.Countdown, () => new CountdownPlugin(time))], store);
        Connect(manager, scheduler);
        await manager.StartAsync();
        check(scheduler.State == IslandState.Compact && scheduler.Pinned?.CompactText == "00:12", "Countdown submits through context during start and enters compact");
        for (var second = 1; second <= 12; second++)
        {
            time.Advance(1);
            await manager.PollAsync();
            if (second == 2) scheduler.MessageArrived(Message);
            scheduler.Tick();
            var expected = second == 12 ? IslandState.Minimal : second is >= 2 and < 7 ? IslandState.Expanded : IslandState.Compact;
            check(scheduler.State == expected && (second == 12 || scheduler.Pinned?.CompactText == $"00:{12 - second:00}"),
                $"Countdown end to end second {second}: running through interruption, return and expiry");
        }
        await manager.RetryAsync(BuiltInPluginIds.Countdown);
        check(scheduler.State == IslandState.Compact && scheduler.Pinned?.CompactText == "00:12", "Countdown retry starts a new fixed content lifecycle");
        scheduler.MessageArrived(Message);
        await manager.SetEnabledAsync(BuiltInPluginIds.Countdown, false);
        check(scheduler.State == IslandState.Expanded && scheduler.Pinned is null, "Disabling countdown removes only pinned content");
        await manager.SetEnabledAsync(BuiltInPluginIds.Countdown, true);
        check(scheduler.Pinned is not null, "Re-enabling creates a fresh countdown");
        await manager.ShutdownAsync();
        check(scheduler.Pinned is null && scheduler.Message is not null, "Shutdown withdraws pinned content before cleanup");
        check(PluginManifestReader.Reserved(BuiltInPluginIds.Countdown) && !BuiltInPluginDefinitions.Countdown.CanRemove,
            "Countdown identity is protected against deletion and external impersonation");
        // 损坏的新增字段不会重置其他偏好。
        var damaged = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "pinned", Guid.NewGuid() + ".json");
        File.WriteAllText(damaged, "{\"Countdown\":\"bad\",\"Animations\":false}");
        var loaded = new SettingsStore(damaged).Current;
        check(!loaded.Countdown && !loaded.Animations, "Invalid countdown preference falls back without resetting valid fields");
    }

    private static async Task CheckIsolation(Action<bool, string> check)
    {
        var store = Store();
        store.Save(store.Current with { Countdown = true });
        var scheduler = new IslandScheduler();
        Probe? source = null;
        using var manager = new PluginManager([new(BuiltInPluginDefinitions.Countdown, () => source = new Probe())], store);
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
        await manager.RetryAsync(BuiltInPluginIds.Countdown);
        oldCapability.Update(Pin);
        check(scheduler.Pinned == Pin && source!.Context != oldContext, "Retry binds a fresh generation and rejects old updates");
        source!.Context!.PublishNotification(new(BuiltInPluginIds.Countdown, "标题", null, "正文", DateTimeOffset.UtcNow, AppName: "Mail"));
        check(scheduler.Message?.AppName == "Mail", "Message delivery remains operational after pinned plugin failure and retry");

        Probe? broken = null;
        using var failing = new PluginManager([new(BuiltInPluginDefinitions.Countdown, () => broken = new Probe { ThrowStart = true })], store);
        var changes = new List<PinnedContentChange>();
        failing.PinnedChanged += changes.Add;
        await failing.StartAsync();
        check(changes.All(c => c.Kind == PinnedChangeKind.Removed) && failing.Snapshot().Single().StatusKey == "Unavailable",
            "Failed startup never publishes its pending pinned item");

        var queue = new Queue<Action>();
        using var queued = new PluginManager([new(BuiltInPluginDefinitions.Countdown, () => new Probe())], store, queue.Enqueue);
        var queuedScheduler = new IslandScheduler();
        Connect(queued, queuedScheduler);
        await queued.StartAsync();
        await queued.SetEnabledAsync(BuiltInPluginIds.Countdown, false);
        while (queue.TryDequeue(out var action)) action();
        check(queuedScheduler.Pinned is null, "Queued background submission after stop is rejected by instance generation");
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
        public string Id => BuiltInPluginIds.Countdown;
        public string DisplayName => "倒计时夹具";
        public event Action<PluginNotification>? NotificationReceived { add { } remove { } }
        public Task InitializeAsync(IPluginContext context) { Context = context; return Task.CompletedTask; }
        public Task StartAsync() { Context!.PinnedContent!.Submit(Pin); if (ThrowStart) throw new InvalidOperationException(); return Task.CompletedTask; }
        public Task PollAsync() { if (ThrowPoll) throw new InvalidOperationException(); return Task.CompletedTask; }
        public Task StopAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
}
