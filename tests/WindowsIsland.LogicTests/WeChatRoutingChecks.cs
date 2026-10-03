using WindowsIsland.Services;

internal static class WeChatRoutingChecks
{
    public static void Run(Action<bool, string> check)
    {
        var time = new ManualTime();
        var router = new MessageRouter(time);
        var automatic = new IslandNotification(1, DateTimeOffset.Now, "微信", "小林", "新的消息",
            Source: NotificationSource.ClientAutomation, AppId: "wechat", EventId: "uia/1");
        var popup = automatic with { Id = 2, Source = NotificationSource.ClientHook, EventId = "popup/1", SenderAvatar = [1, 2, 3], OriginProcessId = 42 };
        check(router.Receive(automatic) is null, "Accessible messages briefly wait for system metadata");
        time.Advance(0.3);
        check(router.Flush().Single().Notification == automatic, "Accessible messages appear without waiting for the popup fallback delay");
        var enrichment = router.Receive(popup);
        check(enrichment is { ReplaceCurrent: true } && enrichment.Notification.SenderAvatar == popup.SenderAvatar,
            "A late client popup enriches the existing accessible notification");
        check(enrichment?.Notification.OriginProcessId == 42, "Late popup metadata preserves the source process for click activation");
        check(router.Receive(popup) is null, "Repeated fallback event identities remain suppressed after merging");
        var repeat = automatic with { Id = 3, EventId = "uia/2" };
        router.Receive(repeat); time.Advance(0.3);
        check(router.Flush().Single().Notification == repeat, "Two distinct accessible messages with identical text are preserved");
        var system = automatic with { Id = 4, Source = NotificationSource.SystemNotification, AppId = "WeChat.Application", EventId = null };
        check(router.Receive(system) is { ReplaceCurrent: true }, "System metadata can update an accessible notification without displaying it again");

        router.Clear();
        router.Receive(popup); router.Receive(automatic); time.Advance(0.3);
        var merged = router.Flush();
        check(merged.Count == 1 && merged[0].Notification.Source == NotificationSource.ClientAutomation
            && merged[0].Notification.SenderAvatar == popup.SenderAvatar, "Popup-first delivery prefers accessible content and preserves available images");
        check(merged[0].Notification.OriginProcessId == 42, "A pending transport merge retains the originating application process");
        var mergedSystem = router.Receive(system);
        check(mergedSystem is { ReplaceCurrent: true } && mergedSystem.Notification.OriginProcessId == 42,
            "System notification enrichment retains the client process for opening the source");
        check(router.Receive(popup) is null && router.Receive(automatic) is null, "Both transport identities stay deduplicated after a pending merge");
        router.Clear();
        router.Receive(automatic); router.Receive(popup);
        router.Receive(repeat); router.Receive(popup with { Id = 5, EventId = "popup/2" }); time.Advance(0.3);
        check(router.Flush().Count == 2, "Transport merging pairs repeated identical messages one for one");
        var settings = new AppSettings(SystemNotifications: false);
        check(settings.Allows(automatic) && !(settings with { WeChat = false }).Allows(automatic),
            "Accessible listening follows the WeChat switch independently of system notifications");

        router.Clear();
        var portrait = automatic with { EventId = "uia/portrait", SenderAvatar = [7, 8, 9] };
        router.Receive(portrait); time.Advance(0.3);
        check(router.Flush().Single().Notification.SenderAvatar == portrait.SenderAvatar, "Accessible portraits reach a standalone notification");
        check(router.Receive(system)?.Notification.SenderAvatar == portrait.SenderAvatar,
            "System metadata preserves an existing accessible conversation portrait");
    }
}
