using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

internal static class PinnedPanelChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var pin = new PinnedContent("panel", BuiltInPluginIds.Countdown, "倒计时", "01:00", Body: "详情");
        var message = new IslandNotification(1, DateTimeOffset.UtcNow, "Mail", "消息", "正文");
        var scheduler = new IslandScheduler();
        scheduler.PinnedAdded(pin);
        scheduler.PinnedClicked();
        check(scheduler.State == IslandState.Expanded && scheduler.ExpandedKind == IslandExpandedKind.Pinned,
            "Compact click expands pinned details when a body exists");
        scheduler.PinnedUpdated(pin with { CompactText = "00:59" });
        check(scheduler.ExpandedKind == IslandExpandedKind.Pinned, "Pinned value update keeps the details panel open");
        scheduler.MessageArrived(message);
        check(scheduler.ExpandedKind == IslandExpandedKind.Message && scheduler.Pinned is not null, "A message closes the pinned panel and takes priority");
        scheduler.MessageDismissed();
        check(scheduler.State == IslandState.Compact && scheduler.ExpandedKind == IslandExpandedKind.None, "Message dismissal never reopens a previous pinned panel");
        foreach (var dismissal in new[] { "outside", "escape", "close" })
        {
            scheduler.PinnedClicked(); scheduler.PanelDismissed();
            check(scheduler.State == IslandState.Compact && scheduler.Pinned is not null, "Panel dismissal preserves compact content: " + dismissal);
        }
        scheduler.PinnedClicked(); scheduler.PinnedRemoved(pin.PluginId);
        check(scheduler.State == IslandState.Minimal, "Pinned removal closes its expanded panel and returns to minimal");
        scheduler.PinnedAdded(pin with { Body = null }); scheduler.PinnedClicked();
        check(scheduler.State == IslandState.Compact, "Click without body or controls does not create an empty panel");
        var controls = new PinnedControl[]
        {
            new("button", "操作", PinnedControlKind.Button),
            new("toggle", "开关", PinnedControlKind.Toggle),
            new("slider", "数值", PinnedControlKind.Slider, Minimum: -5, Maximum: 5, Value: 0, Step: 0.5)
        };
        var interactive = pin with { Body = null, Controls = controls };
        scheduler.PinnedAdded(interactive); scheduler.PinnedClicked();
        check(scheduler.ExpandedKind == IslandExpandedKind.Pinned, "Controls alone allow pinned expansion");
        scheduler.PinnedUpdated(pin with { Body = null });
        check(scheduler.State == IslandState.Compact, "Removing all panel content by update closes empty details");
        var rejected = 0;
        var xaml = "<StackPanel xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' />";
        var manual = PinnedContentPolicy.Normalize(interactive with { CustomPanelXaml = xaml }, pin.PluginId, false, () => rejected++);
        check(manual.CustomPanelXaml is null && manual.Controls!.Count == 3 && rejected == 1,
            "Manual custom XAML is rejected while preserving data driven controls");
        var official = PinnedContentPolicy.Normalize(interactive with { CustomPanelXaml = xaml }, pin.PluginId, true, () => rejected++);
        check(official.CustomPanelXaml == xaml && rejected == 1, "Reviewed custom XAML is retained by host source policy");
        controls[0] = controls[0] with { IsEnabled = false };
        check(manual.Controls![0].IsEnabled, "Control snapshots are copied so plugin mutations cannot bypass validation");
        foreach (var interaction in new PinnedControlInteraction[] { new(pin.Id, "button"), new(pin.Id, "toggle", true), new(pin.Id, "slider", Value: -1.5) })
            check(PinnedContentPolicy.AllowsInteraction(manual, interaction), "Valid panel interaction is accepted: " + interaction.ControlId);
        foreach (var interaction in new PinnedControlInteraction[]
        {
            new("stale", "button"), new(pin.Id, "unknown"), new(pin.Id, "button", true), new(pin.Id, "toggle"),
            new(pin.Id, "slider", Value: double.NaN), new(pin.Id, "slider", Value: 6), new(pin.Id, "slider", Value: 0.1)
        }) check(!PinnedContentPolicy.AllowsInteraction(manual, interaction), "Invalid panel interaction is ignored: " + interaction);
        var encoded = JsonSerializer.Deserialize<PinnedContent>(JsonSerializer.Serialize(official))!;
        check(encoded.Controls!.SequenceEqual(official.Controls!) && encoded.CustomPanelXaml == xaml, "Panel controls and optional XAML round trip in Contracts");
        var invalid = false;
        try { PinnedContentPolicy.Normalize(pin with { Controls = [new("x", "坏值", PinnedControlKind.Slider, Step: 0)] }, pin.PluginId, true, () => { }); }
        catch (InvalidDataException) { invalid = true; }
        check(invalid, "Invalid slider models are rejected before reaching WinUI");

        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "pinned-panels", Guid.NewGuid().ToString("N"));
        Diagnostics.Initialize(Path.Combine(root, "diagnostics"), true);
        try
        {
            var store = new SettingsStore(Path.Combine(root, "settings.json"));
            store.Save(store.Current with { Countdown = true });
            var clock = new Clock();
            store.SetPluginString(pin.PluginId, "DurationSeconds", "60");
            using var timer = new PluginManager([new(BuiltInPluginDefinitions.Countdown, () => new CountdownPlugin(clock))], store);
            PinnedContent? current = null;
            timer.PinnedChanged += change => current = change.Content;
            await timer.StartAsync();
            await timer.InteractPinnedAsync(current!, new("countdown", "pause", true));
            check(current?.ExpiresAt is null && current?.Controls?.Single(c => c.Id == "pause").IsOn == true,
                "Countdown receives the pause interaction through its context callback");
            clock.Seconds += 20;
            await timer.PollAsync();
            check(current?.CompactText == "01:00", "Paused countdown keeps its value while elapsed wall time advances");
            await timer.InteractPinnedAsync(current!, new("countdown", "pause", false));
            check(current?.ExpiresAt == clock.GetUtcNow().AddSeconds(60), "Resume establishes a new deadline from remaining time");
            await timer.InteractPinnedAsync(current!, new("countdown", "duration", Value: 90));
            check(current?.CompactText == "01:30" && store.GetPluginString(pin.PluginId, "DurationSeconds", null) == "90", "Slider interaction changes countdown duration and publishes the updated controls");

            var manualId = "wini_manual_panel_plugin";
            var paths = new PluginPaths(root);
            var manifest = new PluginManifest(manualId, "手动面板", "1.0.0", "fixture.dll", "Fixture", 1, [PluginPermissions.Notifications]);
            var external = new ExternalPlugin(paths, manifest, paths.Code(PluginSourceKind.Manual, manualId));
            var definition = new NotificationPluginDefinition(manualId, "Manual", "ManualToggle", 0, DiagnosticArea.Application,
                _ => true, (s, _) => s, _ => false, new(false, "1.0.0", "fixture.dll", External: external));
            var probe = new Source(manualId, xaml);
            using var manager = new PluginManager([new(definition, () => probe)], store);
            PinnedContent? visible = null;
            manager.PinnedChanged += change => visible = change.Content;
            manager.Received += _ => probe.MessageCount++;
            await manager.StartAsync();
            check(visible is { CustomPanelXaml: null, Body: "正文" } && Diagnostics.Store!.Snapshot().Any(e => e.Operation == "RejectPinnedXaml" && e.PluginId == manualId),
                "Manual XAML rejection records structured diagnostics and still publishes ordinary content");
            await manager.InteractPinnedAsync(visible!, new("panel", "button"));
            check(probe.Interactions == 1 && probe.MessageCount == 1, "Manual plugin keeps working after rejected custom XAML");
            scheduler.PinnedAdded(visible!); scheduler.PinnedClicked();
            manager.PinnedChanged += change => { if (change.Kind == PinnedChangeKind.Removed) scheduler.PinnedRemoved(manualId); };
            probe.ThrowInteraction = true;
            await manager.InteractPinnedAsync(visible!, new("panel", "button"));
            check(scheduler.State == IslandState.Minimal && manager.Snapshot().Single().StatusKey == "Unavailable", "Interaction failure closes its panel and isolates the failed plugin");

            var pending = new Source(manualId, xaml)
            {
                Entered = new(TaskCreationOptions.RunContinuationsAsynchronously),
                Continue = new(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            using var delayed = new PluginManager([new(definition, () => pending)], store);
            PinnedContent? pendingContent = null;
            delayed.PinnedChanged += change => pendingContent = change.Content;
            await delayed.StartAsync();
            var interaction = delayed.InteractPinnedAsync(pendingContent!, new("panel", "button"));
            await pending.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var shutdown = delayed.ShutdownAsync();
            check(pendingContent is null && !pending.Stopped && !shutdown.IsCompleted, "Stopping immediately withdraws content while waiting for an in-flight interaction");
            pending.Continue.SetResult();
            await Task.WhenAll(interaction, shutdown).WaitAsync(TimeSpan.FromSeconds(5));
            check(pending.Stopped && pending.Disposed && pending.MessageCount == 0, "Cleanup waits for the callback and rejects its late notification after revocation");
        }
        finally { Diagnostics.Shutdown(); }
    }
    private sealed class Clock : TimeProvider
    {
        public int Seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(Seconds);
    }
    private sealed class Source(string id, string xaml) : IMessageSource
    {
        private IPluginContext? _context;
        public int Interactions, MessageCount;
        public bool ThrowInteraction;
        public TaskCompletionSource? Entered, Continue;
        public bool Stopped, Disposed;
        public string Id => id;
        public string DisplayName => "手动面板";
        public event Action<PluginNotification>? NotificationReceived { add { } remove { } }
        public Task InitializeAsync(IPluginContext context)
        {
            _context = context;
            context.PinnedContent!.SetInteractionHandler(async _ =>
            {
                if (ThrowInteraction) throw new InvalidOperationException();
                Entered?.TrySetResult();
                if (Continue is not null) await Continue.Task;
                Interactions++;
                context.PublishNotification(new(id, "消息", null, "正文", DateTimeOffset.UtcNow));
            });
            return Task.CompletedTask;
        }
        public Task StartAsync()
        {
            _context!.PinnedContent!.Submit(new("panel", id, "详情", "00:10", Body: "正文",
                Controls: [new("button", "操作", PinnedControlKind.Button)], CustomPanelXaml: xaml));
            return Task.CompletedTask;
        }
        public Task StopAsync() { Stopped = true; return Task.CompletedTask; }
        public void Dispose() { Disposed = true; }
    }
}
