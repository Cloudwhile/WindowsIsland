using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

internal static class PluginChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var settings = new AppSettings(SystemNotifications: false, WeChat: true, Telegram: true, Power: false);
        var weChat = new List<FakePlugin>();
        var telegram = new List<FakePlugin>();
        using var host = new PluginManager([
            new(BuiltInPluginDefinitions.WeChat, () => Add(weChat, "wechat")),
            new(BuiltInPluginDefinitions.Telegram, () => Add(telegram, "telegram")),
            new(BuiltInPluginDefinitions.Power, () => throw new InvalidOperationException("Disabled factory ran"))
        ], Store(settings));
        var messages = new List<IslandNotification>();
        host.Received += n => { if (host.Allows(n)) messages.Add(n); };
        await host.StartAsync();
        await host.StartAsync();
        check(weChat.Count == 1 && telegram.Count == 1 && weChat[0].Starts == 1,
            "Enabled plugins start once and disabled factories are not invoked");
        await host.ApplySettingsAsync(settings with { Animations = false, Language = "en-US", IncludePrereleaseUpdates = true });
        check(weChat.Count == 1 && telegram.Count == 1 && weChat[0].Disposals == 0,
            "Appearance, language and update preferences preserve plugin instances and baselines");
        foreach (var appearance in new[] { settings with { Acrylic = false }, settings with { OpacityTransitions = false },
            settings with { BackgroundOpacity = 0.4 } })
        {
            await host.ApplySettingsAsync(appearance);
            check(weChat.Count == 1 && telegram.Count == 1 && weChat[0].Starts == 1 && weChat[0].Disposals == 0
                && telegram[0].Starts == 1 && telegram[0].Disposals == 0,
                "Changing popup material or opacity preserves active source instances and baselines");
        }
        var chatMessage = Message("wechat");
        weChat[0].Emit(chatMessage);
        check(messages.Count == 1, "Plugin messages flow through the host");
        weChat[0].Allow = false;
        check(!host.Allows(chatMessage), "Conversation policy applies to client messages");
        var toast = chatMessage with { Source = NotificationSource.SystemNotification };
        await host.ApplySettingsAsync(settings with { SystemNotifications = true });
        check(!host.Allows(toast), "Conversation policy also applies to Windows toast messages");
        weChat[0].Allow = true;
        check(host.Allows(toast), "Allowed conversations pass the same cross-source policy");
        var staleCallback = weChat[0].Received;
        await host.ApplySettingsAsync(settings with { WeChat = false });
        staleCallback?.Invoke(PluginNotificationBridge.ToContract("wechat", chatMessage));
        check(weChat[0].Disposals == 1 && weChat[0].Received is null && messages.Count == 1,
            "Disabling a plugin detaches events and rejects already queued callbacks");
        telegram[0].Emit(Message("telegram"));
        check(messages.Count == 2 && telegram[0].Disposals == 0, "Disabling WeChat leaves Telegram running");
        await host.ApplySettingsAsync(settings);
        check(weChat.Count == 2 && telegram.Count == 1 && weChat[1].Starts == 1,
            "Re-enabling creates only the selected plugin with a fresh baseline");
        await host.PollAsync();
        check(weChat[1].Polls == 1 && telegram[0].Polls == 1, "Host polling reaches each active plugin");
        var definitions = new[] { BuiltInPluginDefinitions.System, BuiltInPluginDefinitions.WeChat,
            BuiltInPluginDefinitions.Telegram, BuiltInPluginDefinitions.Power };
        foreach (var definition in definitions)
        {
            var disabled = definition.SetEnabled(new AppSettings(), false);
            check(!definition.IsEnabled(disabled) && definitions.Where(d => d != definition).All(d => d.IsEnabled(disabled)),
                "Plugin preference remains independent and uses its existing field: " + definition.Id);
        }
        check(host.Snapshot().Select(s => s.Definition.Id).SequenceEqual(new[] { "wechat", "telegram", "power" })
            && host.Snapshot()[2].StatusKey == "Off", "Settings consume plugin metadata and current status");
        host.Dispose();
        host.Dispose();
        await host.PollAsync();
        check(weChat[1].Disposals == 1 && telegram[0].Disposals == 1 && !host.Allows(chatMessage),
            "Host disposal is idempotent and stops all routing");

        var duplicateRejected = false;
        try
        {
            using var duplicate = new PluginManager([
                new(BuiltInPluginDefinitions.WeChat, () => new FakePlugin()),
                new(BuiltInPluginDefinitions.WeChat with { Id = "WECHAT" }, () => new FakePlugin())], Store(settings));
        }
        catch (ArgumentException) { duplicateRejected = true; }
        check(duplicateRejected, "Duplicate plugin IDs are rejected before factories run");

        await CheckFailures(check, settings);
        await CheckPendingStart(check, settings);
        await CheckAccess(check);
    }

    private static async Task CheckFailures(Action<bool, string> check, AppSettings settings)
    {
        var bad = new FakePlugin { FailStart = true, FailDispose = true };
        var good = new FakePlugin("telegram");
        using var host = new PluginManager([
            new(BuiltInPluginDefinitions.WeChat, () => bad), new(BuiltInPluginDefinitions.Telegram, () => good)], Store(settings));
        await host.StartAsync();
        check(good.Starts == 1 && bad.Disposals == 1 && host.Snapshot()[0].StatusKey == "Unavailable",
            "Startup and disposal failures do not stop other plugins and expose unavailable status");
        check(!host.Allows(Message("wechat")), "Failed plugin policies reject their source");
        bad.FailStart = bad.FailDispose = false;
        await host.ApplySettingsAsync(settings with { WeChat = false });
        await host.ApplySettingsAsync(settings);
        check(bad.Starts == 2, "Explicit re-enable retries a failed plugin");
        bad.FailPoll = true;
        await host.PollAsync();
        check(good.Polls == 1 && bad.Disposals == 2, "Polling failure is isolated and its resources are released");
        check(host.Snapshot()[0] is { StatusKey: "Unavailable", CanRetry: true } && !host.Snapshot()[1].CanRetry,
            "Only failed sources offer a retry");
        bad.FailPoll = false;
        await host.RetryAsync(BuiltInPluginDefinitions.WeChat.Id);
        check(bad.Starts == 3 && host.Snapshot()[0] is { StatusKey: "Connected", CanRetry: false }, "Retrying a failed source recreates it");
        good.FailFilter = true;
        check(!host.Allows(Message("telegram")) && good.Disposals == 1, "Filter failure fails closed and releases the plugin");
        using var factoryHost = new PluginManager([
            new(BuiltInPluginDefinitions.WeChat, () => throw new InvalidOperationException()),
            new(BuiltInPluginDefinitions.Telegram, () => new FakePlugin("telegram"))], Store(settings));
        await factoryHost.StartAsync();
        check(factoryHost.Snapshot()[0].StatusKey == "Unavailable" && factoryHost.Snapshot()[1].StatusKey == "Connected",
            "Factory failure is isolated from later plugins");
    }

    private static async Task CheckPendingStart(Action<bool, string> check, AppSettings settings)
    {
        var started = new TaskCompletionSource();
        var slow = new FakePlugin { Starting = started.Task };
        var next = new FakePlugin();
        var creates = 0;
        using var host = new PluginManager([new(BuiltInPluginDefinitions.WeChat, () => ++creates == 1 ? slow : next)], Store(settings));
        var start = host.StartAsync();
        var stale = slow.Received;
        var received = 0;
        host.Received += _ => received++;
        var disable = host.ApplySettingsAsync(settings with { WeChat = false });
        var enable = host.ApplySettingsAsync(settings);
        stale?.Invoke(PluginNotificationBridge.ToContract("wechat", Message("wechat")));
        check(slow.Disposals == 1 && received == 0, "Disable invalidates a plugin while its start is awaiting");
        started.SetException(new InvalidOperationException("Old start failed"));
        await Task.WhenAll(start, disable, enable);
        check(creates == 2 && next.Starts == 1 && host.Snapshot()[0].StatusKey == "Connected",
            "Late failure from a disabled generation does not poison its replacement");
        var poll = new TaskCompletionSource();
        next.Polling = poll.Task;
        var pending = host.PollAsync();
        await host.PollAsync();
        host.Dispose();
        poll.SetResult();
        await pending;
        check(next.Polls == 1 && next.Disposals == 1, "Overlapping polls are skipped and shutdown during an await is safe");
    }

    private static async Task CheckAccess(Action<bool, string> check)
    {
        var plugin = new AccessPlugin();
        using var host = new PluginManager([new(BuiltInPluginDefinitions.System, () => plugin)], Store(new AppSettings()));
        var accessChanges = 0;
        host.AccessChanged += _ => accessChanges++;
        await host.StartAsync();
        await host.RequestAccessAsync();
        check(host.Access == NotificationAccess.Allowed && accessChanges == 1 && plugin.Requests == 1,
            "Optional notification permission capability flows through the host");
        host.Dispose();
        plugin.Change(PluginNotificationAccess.Denied);
        check(accessChanges == 1, "Permission callbacks are detached at shutdown");
    }

    private static SettingsStore Store(AppSettings settings)
    {
        var store = new SettingsStore(Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "plugin-manager",
            Guid.NewGuid().ToString("N"), "settings.json"));
        store.Save(settings);
        return store;
    }

    private static FakePlugin Add(List<FakePlugin> plugins, string id) { var plugin = new FakePlugin(id); plugins.Add(plugin); return plugin; }
    private static IslandNotification Message(string app) => new(1, DateTimeOffset.UtcNow, app, "conversation", "body",
        Source: NotificationSource.ClientAutomation, AppId: app);

    private class FakePlugin(string id = "wechat") : IMessageSource, IPluginPolling, IPluginNotificationFilter, IPluginStatusProvider
    {
        // A field allows capturing a deliberately stale callback to test the host boundary.
        public Action<PluginNotification>? Received;
        event Action<PluginNotification>? IMessageSource.NotificationReceived { add => Received += value; remove => Received -= value; }
        public int Starts, Polls, Disposals;
        public bool Allow = true, FailStart, FailPoll, FailDispose, FailFilter;
        public Task? Starting, Polling;
        public string Id => id;
        public string DisplayName => id;
        public PluginStatus Status => new(PluginConnectionState.Connected);
        public Task InitializeAsync(IPluginContext context) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task StartAsync() { Starts++; if (FailStart) throw new InvalidOperationException(); return Starting ?? Task.CompletedTask; }
        public Task PollAsync() { Polls++; if (FailPoll) throw new InvalidOperationException(); return Polling ?? Task.CompletedTask; }
        public bool Allows(PluginNotification _) { if (FailFilter) throw new InvalidOperationException(); return Allow; }
        public void Dispose() { Disposals++; if (FailDispose) throw new InvalidOperationException(); }
        public void Emit(IslandNotification notification) => Received?.Invoke(PluginNotificationBridge.ToContract(Id, notification));
    }
    private sealed class AccessPlugin() : FakePlugin("system"), IPluginNotificationAccess
    {
        public PluginNotificationAccess Access { get; private set; } = PluginNotificationAccess.Waiting;
        public int Requests;
        public event Action<PluginNotificationAccess>? AccessChanged;
        public Task RequestAccessAsync() { Requests++; Change(PluginNotificationAccess.Allowed); return Task.CompletedTask; }
        public void Change(PluginNotificationAccess value) { Access = value; AccessChanged?.Invoke(value); }
    }
}
