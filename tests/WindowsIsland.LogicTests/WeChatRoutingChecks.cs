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

        router.Clear();
        var lateAvatar = automatic with { SenderAvatar = [3, 4, 5], IsMetadataUpdate = true };
        check(router.Receive(lateAvatar) is null && !router.HasPending,
            "An avatar update without its original notification cannot create a popup");
        router.Receive(automatic);
        check(router.Receive(lateAvatar) is null, "An early avatar update enriches the pending notification silently");
        time.Advance(0.3);
        check(router.Flush().Single().Notification.SenderAvatar == lateAvatar.SenderAvatar,
            "An avatar that arrives during the merge delay appears on the first popup");
        router.Clear(); router.Receive(automatic); time.Advance(0.3); router.Flush();
        check(router.Receive(lateAvatar) is { ReplaceCurrent: true } update && update.Notification.SenderAvatar == lateAvatar.SenderAvatar,
            "An avatar arriving after display refreshes the existing popup without restarting it");
        check(router.Receive(lateAvatar) is null, "Repeated avatar enrichment is suppressed");
        time.Advance(9);
        check(router.Receive(lateAvatar) is null && !router.HasPending,
            "Expired avatar enrichment never replays an old notification");

        var now = DateTimeOffset.UtcNow;
        var session = new WeChatPreview("stable-session", automatic.Title, automatic.Body, 1, "刚刚");
        var enrichmentQueue = new WeChatAvatarEnrichment();
        enrichmentQueue.Remember(automatic, [session], now);
        check(enrichmentQueue.Update([session], now).Count == 0, "A missing avatar remains pending without delaying the message");
        var completed = enrichmentQueue.Update([session with { Avatar = [8, 9], Body = "本人在其他设备上的回复" }], now.AddSeconds(1));
        check(completed.Count == 1 && completed[0].IsMetadataUpdate && completed[0].EventId == automatic.EventId
            && completed[0].CreatedAt == automatic.CreatedAt && completed[0].Body == automatic.Body,
            "Late portraits preserve the original event and body even after a remote-device reply changes the preview");
        check(enrichmentQueue.Update([session with { Avatar = [8, 9] }], now.AddSeconds(2)).Count == 0,
            "A pending avatar is completed only once");
        enrichmentQueue.Remember(automatic, [session], now);
        check(enrichmentQueue.Update([session with { Conversation = "另一会话", Avatar = [8, 9] }], now.AddSeconds(1)).Count == 0,
            "A recycled row cannot supply another conversation's avatar");
        enrichmentQueue.Remember(automatic, [session], now);
        check(enrichmentQueue.Update([session with { Muted = true, Avatar = [8, 9] }], now.AddSeconds(1)).Count == 0,
            "Enabling mute cancels pending avatar enrichment");
        enrichmentQueue.Remember(automatic, [session], now);
        check(enrichmentQueue.Update([session with { Avatar = [8, 9] }], now.AddSeconds(6)).Count == 0,
            "Avatar waiting is bounded to the notification display duration");
    }
}
