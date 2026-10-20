using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.LogicTests;

internal static class PluginManagerChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "plugin-contract-manager", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(path, """
            {"SystemNotifications":false,"WeChat":true,"Telegram":false,"Power":true,"Animations":false,"Language":"en-US"}
            """);
        var store = new SettingsStore(path);
        var created = new Dictionary<string, List<Source>>(StringComparer.Ordinal);
        var definitions = new[] { BuiltInPluginDefinitions.System, BuiltInPluginDefinitions.Telegram,
            BuiltInPluginDefinitions.Power, BuiltInPluginDefinitions.WeChat };
        using var manager = new PluginManager(definitions.Select(definition => new NotificationPluginRegistration(definition, () =>
        {
            var plugin = new Source(definition.Id);
            if (!created.TryGetValue(definition.Id, out var sources)) created[definition.Id] = sources = [];
            sources.Add(plugin);
            return plugin;
        })), store);
        await manager.StartAsync();
        check(created.Keys.Order().SequenceEqual(new[] { "power", "wechat" }),
            "Legacy source switches select plugin activation without changing their values");
        check(created.Values.SelectMany(s => s).All(s => s.Calls.SequenceEqual(["Initialize", "Start"])),
            "Plugin registration initializes its own context before starting exactly once");
        await manager.StartAsync();
        check(created.Values.All(s => s.Count == 1), "Repeated manager startup keeps existing plugin instances");
        await manager.SetEnabledAsync("wechat", false);
        check(created["wechat"][0].Calls.SequenceEqual(["Initialize", "Start", "Stop", "Dispose"])
            && created["power"][0].Calls.Count == 2, "Disabling stops then disposes only the selected plugin");
        await manager.SetEnabledAsync("wechat", true);
        check(created["wechat"].Count == 2 && created["wechat"][1].Calls.SequenceEqual(["Initialize", "Start"]),
            "Re-enabling initializes a fresh source instance");
        var restarted = new SettingsStore(path);
        check(!restarted.Current.SystemNotifications && !restarted.Current.Telegram && restarted.Current.WeChat && restarted.Current.Power
            && !restarted.Current.Animations && restarted.Current.Language == "en-US", "Plugin toggles persist in legacy fields without resetting unrelated preferences");

        var wechat = created["wechat"][1].Context!;
        var power = created["power"][0].Context!;
        wechat.Settings.SetString("mode", "会话监听");
        wechat.Settings.SetBoolean("enabled", false);
        power.Settings.SetString("mode", "电源事件");
        check(wechat.Settings.GetString("mode") == "会话监听" && power.Settings.GetString("mode") == "电源事件"
            && !wechat.Settings.GetBoolean("enabled", true) && power.Settings.GetBoolean("enabled", true),
            "Contexts isolate string and boolean settings by plugin ID");
        store.Save(store.Current with { Acrylic = false });
        restarted = new SettingsStore(path);
        var saved = new PluginSettings(restarted, "wechat");
        check(saved.GetString("mode") == "会话监听" && !saved.GetBoolean("enabled", true) && !restarted.Current.Acrylic,
            "Host preference writes and restart preserve plugin namespaces");
        saved.SetString("mode", null);
        check(new PluginSettings(new SettingsStore(path), "wechat").GetString("mode", "fallback") == "fallback",
            "Deleting a plugin string persists removal without removing other keys");
        Parallel.For(0, 20, index => wechat.Settings.SetString("key" + index, index.ToString()));
        check(Enumerable.Range(0, 20).All(index => new PluginSettings(new SettingsStore(path), "wechat").GetString("key" + index) == index.ToString()),
            "Concurrent plugin setting writes preserve independent keys");
        await File.WriteAllTextAsync(path, """
            {"SystemNotifications":false,"WeChat":true,"Plugins":{"wechat":{"valid":"保持","bad":17,"enabled":"broken"},"power":false}}
            """);
        var damaged = new SettingsStore(path);
        var damagedSettings = new PluginSettings(damaged, "wechat");
        check(damagedSettings.GetString("valid") == "保持" && damagedSettings.GetString("bad", "fallback") == "fallback"
            && damagedSettings.GetBoolean("enabled", true) && damaged.Current.WeChat && !damaged.Current.SystemNotifications,
            "Damaged plugin values fall back individually and retain valid legacy preferences");

        foreach (var source in Enum.GetValues<NotificationSource>())
        {
            var notification = new IslandNotification(123, DateTimeOffset.Now, "QQ", "会话", "正文", [1, 2], source,
                "QQ.App", "event/123", "symbol", [3, 4], 1234, true);
            check(PluginNotificationBridge.ToHost(PluginNotificationBridge.ToContract("system", notification)) == notification,
                "Notification conversion preserves all pipeline fields and source priority: " + source);
        }
        var qq = PluginNotificationBridge.ToHost(new("system", "QQ 会话", null, "QQ 正文", DateTimeOffset.Now, AppId: "Tencent.QQ"));
        check(qq.Source == NotificationSource.SystemNotification && new AppSettings().Allows(qq), "QQ retains the system notification route");
        await CheckFailures(root, check);
        await CheckPending(root, check);
        await CheckBuiltIns(root, check);
    }

    private static async Task CheckFailures(string root, Action<bool, string> check)
    {
        Diagnostics.Initialize(Path.Combine(root, "diagnostics"), true);
        try
        {
            foreach (var stage in new[] { "Initialize", "Start", "Callback", "Dispatch", "Identity" })
            {
                var good = new Source("telegram");
                var bad = new Source("wechat") { FailAt = stage };
                var store = new SettingsStore(Path.Combine(root, stage + ".json"));
                using var manager = new PluginManager([
                    new(BuiltInPluginDefinitions.WeChat, () => bad), new(BuiltInPluginDefinitions.Telegram, () => good)
                ], store, action => { if (stage == "Dispatch") throw new InvalidOperationException(); action(); });
                await manager.StartAsync();
                if (stage == "Callback") manager.Received += _ => throw new InvalidOperationException();
                if (stage is "Callback" or "Dispatch" or "Identity")
                    bad.Context!.PublishNotification(Message(stage == "Identity" ? "telegram" : "wechat"));
                check(manager.Snapshot()[0] is { StatusKey: "Unavailable", CanRetry: true } && good.Calls.Contains("Start")
                    && !good.Calls.Contains("Dispose") && bad.Calls.TakeLast(2).SequenceEqual(["Stop", "Dispose"]),
                    "A plugin failure stops only its own generation: " + stage);
            }
            var diagnostic = new PluginDiagnostics("wechat", DiagnosticArea.WeChat);
            diagnostic.Write(new("ignored", "Metadata", true, new("Example.Error", -123, "4.1.2")));
            var entries = Diagnostics.Store!.Snapshot();
            check(entries.Any(e => e.PluginId == "wechat" && e.Operation == "Initialize")
                && entries.Any(e => e.PluginId == "wechat" && e.Operation == "Callback"),
                "Initialize and push callback failures enter local diagnostics with plugin identity");
            var metadata = entries.Single(e => e.Operation == "Metadata");
            check(metadata is { Area: DiagnosticArea.WeChatPlugin, Fatal: true, ClientVersion: "4.1.2" }
                && metadata.Errors.Single() is { Type: "Example.Error", HResult: -123 },
                "Diagnostic bridge preserves fatal flag and structured error metadata");
            using var report = JsonDocument.Parse(Diagnostics.Store.ReportJson());
            check(report.RootElement.GetProperty("Events").EnumerateArray().All(e => e.GetProperty("Area").GetString() == "wechat"),
                "Plugin diagnostic report areas use stable plugin IDs");
            check(DiagnosticPresentation.Title(metadata).Length > 0, "Plugin diagnostic area names retain localized report titles");
            Diagnostics.Store.SetEnabled(false);
            diagnostic.Write(new("ignored", "Disabled"));
            check(Diagnostics.Store.Snapshot().Length == entries.Length, "Plugin diagnostics respect the existing collection preference");
        }
        finally { Diagnostics.Shutdown(); }
    }

    private static async Task CheckPending(string root, Action<bool, string> check)
    {
        var queue = new Queue<Action>();
        var source = new Source("wechat");
        var store = new SettingsStore(Path.Combine(root, "pending.json"));
        using var manager = new PluginManager([new(BuiltInPluginDefinitions.WeChat, () => source)], store, queue.Enqueue);
        var received = 0;
        manager.Received += _ => received++;
        await manager.StartAsync();
        var oldContext = source.Context!;
        await Task.Run(() => oldContext.PublishNotification(Message("wechat")));
        check(received == 0 && queue.Count == 1, "Background notifications use the host dispatcher before touching the pipeline");
        while (queue.TryDequeue(out var action)) action();
        check(received == 1, "Host dispatcher forwards a notification once");
        oldContext.PublishNotification(Message("wechat"));
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Stopping = stopping.Task;
        var disable = manager.SetEnabledAsync("wechat", false);
        while (queue.TryDequeue(out var action)) action();
        oldContext.PublishNotification(Message("wechat"));
        check(received == 1 && !source.Calls.Contains("Dispose"), "Disable immediately revokes queued and late callbacks while asynchronous stop is pending");
        stopping.SetResult();
        await disable;
        check(source.Calls.TakeLast(2).SequenceEqual(["Stop", "Dispose"]), "Dispose waits until asynchronous Stop has completed");

        var initializing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new Source("wechat") { Initializing = initializing.Task };
        using var pending = new PluginManager([new(BuiltInPluginDefinitions.WeChat, () => slow)], new SettingsStore(Path.Combine(root, "initialize.json")));
        var start = pending.StartAsync();
        pending.Dispose();
        initializing.SetResult();
        await start;
        check(!slow.Calls.Contains("Start") && slow.Calls.TakeLast(2).SequenceEqual(["Stop", "Dispose"]),
            "Shutdown during Initialize rejects a late Start and releases the generation");
    }

    private static async Task CheckBuiltIns(string root, Action<bool, string> check)
    {
        var definitions = new[] { BuiltInPluginDefinitions.Power, BuiltInPluginDefinitions.System,
            BuiltInPluginDefinitions.Telegram, BuiltInPluginDefinitions.WeChat };
        Func<IMessageSource>[] factories = [() => new PowerPlugin(), () => new SystemNotificationPlugin(),
            () => new TelegramPlugin(), () => new WeChatPlugin()];
        var backends = new Dictionary<string, Backend>();
        var store = new SettingsStore(Path.Combine(root, "builtins.json"));
        using var manager = new PluginManager(definitions.Select((d, i) => new NotificationPluginRegistration(d, factories[i])),
            store, backends: id => backends[id] = new Backend());
        var received = 0;
        manager.Received += _ => received++;
        await manager.StartAsync();
        foreach (var definition in definitions)
        {
            var backend = backends[definition.Id];
            backend.Emit(Message(definition.Id));
            check(backend.Starts == 1, "Built-in source initializes its context backend: " + definition.Id);
            await manager.SetEnabledAsync(definition.Id, false);
            backend.Emit(Message(definition.Id));
            check(backend.Disposals == 1, "Built-in source releases its backend exactly once: " + definition.Id);
            await manager.SetEnabledAsync(definition.Id, true);
            check(!ReferenceEquals(backend, backends[definition.Id]) && backends[definition.Id].Starts == 1,
                "Built-in source re-enable acquires a fresh backend and baseline: " + definition.Id);
        }
        check(received == 4, "All four built-in adapters push through the context and reject notifications after disabling");
        check(manager.Access == NotificationAccess.Allowed, "System source forwards initial notification permission results");
        backends["system"].ChangeAccess(PluginNotificationAccess.Denied);
        check(manager.Access == NotificationAccess.Denied, "System source forwards subsequent permission changes");
        backends["wechat"].Status = new(PluginConnectionState.Disconnected, PluginStatusReason.VersionNotSupported);
        check(manager.Snapshot().Single(s => s.Definition.Id == "wechat") is { StatusKey: "WeChatUnsupported", CanRetry: true },
            "WeChat degraded status preserves the unsupported-version reason and retry action");
        await manager.RetryAsync("wechat");
        check(backends["wechat"].Retries == 1, "WeChat retry reaches only its own context backend");

        var time = new ManualTime();
        var router = new MessageRouter(time);
        var routed = new List<MessageDispatch?>();
        manager.Received += notification => routed.Add(router.Receive(notification));
        var message = Message("wechat") with { AppId = "wechat", DeduplicationKey = "uia/123/456/1",
            Kind = PluginNotificationKind.ClientAutomation };
        backends["wechat"].Emit(message);
        time.Advance(0.3);
        var first = router.Flush().Single().Notification;
        backends["wechat"].Emit(message);
        time.Advance(0.3);
        check(router.Flush().Count == 0, "WeChat adapter preserves duplicate suppression within one listener instance");
        backends["wechat"].Emit(message with { IsMetadataUpdate = true, SenderAvatar = [9] });
        check(routed[^1] is { ReplaceCurrent: true, Notification.SenderAvatar: [9] } update
            && update.Notification.EventId == first.EventId && router.Flush().Count == 0,
            "WeChat avatar completion retains the original event identity without creating an extra notification");
        await manager.SetEnabledAsync("wechat", false);
        await manager.SetEnabledAsync("wechat", true);
        backends["wechat"].Emit(message with { Body = "重新启用后的第一条消息" });
        time.Advance(0.3);
        check(router.Flush().Single().Notification is { Body: "重新启用后的第一条消息" } next && next.EventId != first.EventId,
            "Rapid WeChat re-enable does not mistake a reset listener sequence for an old event");
        await manager.ShutdownAsync();
        check(backends.Values.All(b => b.Disposals == 1), "Application shutdown releases every backend once");
    }

    private static PluginNotification Message(string id) => new(id, "会话", "发送者", "正文", DateTimeOffset.Now);

    private sealed class Source(string id) : IMessageSource, IPluginStatusProvider
    {
        public string Id => id;
        public string DisplayName => id;
        public readonly List<string> Calls = [];
        public string? FailAt;
        public Task? Initializing, Stopping;
        public IPluginContext? Context;
        public PluginStatus Status => new(PluginConnectionState.Connected);
        public event Action<PluginNotification>? NotificationReceived { add { } remove { } }
        public Task InitializeAsync(IPluginContext context)
        {
            Context = context; Calls.Add("Initialize");
            if (FailAt == "Initialize") throw new InvalidOperationException();
            return Initializing ?? Task.CompletedTask;
        }
        public Task StartAsync()
        {
            Calls.Add("Start");
            if (FailAt == "Start") throw new InvalidOperationException();
            return Task.CompletedTask;
        }
        public Task StopAsync() { Calls.Add("Stop"); return Stopping ?? Task.CompletedTask; }
        public void Dispose() => Calls.Add("Dispose");
    }

    private sealed class Backend : IMessageSourceBackend, IPluginNotificationAccess, IPluginRetry
    {
        public int Starts, Disposals, Retries;
        private bool _disposed;
        public PluginStatus Status { get; set; } = new(PluginConnectionState.Connected);
        public PluginNotificationAccess Access { get; private set; } = PluginNotificationAccess.Allowed;
        public bool CanRetry => true;
        public event Action<PluginNotification>? NotificationReceived;
        public event Action<PluginNotificationAccess>? AccessChanged;
        public Task StartAsync() { Starts++; return Task.CompletedTask; }
        public Task PollAsync() => Task.CompletedTask;
        public bool Allows(PluginNotification notification) => true;
        public Task RequestAccessAsync() => Task.CompletedTask;
        public void ChangeAccess(PluginNotificationAccess access) { Access = access; AccessChanged?.Invoke(access); }
        public void Retry() => Retries++;
        public void Emit(PluginNotification notification) => NotificationReceived?.Invoke(notification);
        public void Dispose() { if (_disposed) return; _disposed = true; Disposals++; }
    }
}
