using WindowsIsland.Services;

internal static class GameModeChecks
{
    public static void Run(Action<bool, string> check)
    {
        var policy = new GameModePolicy();
        var scenarios = new (UserNotificationState State, NotificationMuteReason[] Reasons)[]
        {
            (UserNotificationState.Unknown, [0, 0, 0, 0]),
            (UserNotificationState.NotPresent, [0, 0, 0, 0]),
            (UserNotificationState.Busy, [0, NotificationMuteReason.FullScreen, 0, NotificationMuteReason.FullScreen]),
            (UserNotificationState.RunningDirect3DFullScreen, [0, NotificationMuteReason.FullScreen,
                NotificationMuteReason.Game, NotificationMuteReason.Game | NotificationMuteReason.FullScreen]),
            (UserNotificationState.PresentationMode, [0, 0, 0, 0]),
            (UserNotificationState.AcceptsNotifications, [0, 0, 0, 0]),
            (UserNotificationState.QuietTime, [0, 0, 0, 0]),
            (UserNotificationState.App, [0, 0, 0, 0]),
            ((UserNotificationState)999, [0, 0, 0, 0])
        };
        foreach (var (state, reasons) in scenarios)
        {
            var combination = 0;
            foreach (var games in new[] { false, true })
            foreach (var fullScreen in new[] { false, true })
            {
                var reason = reasons[combination++];
                policy.Update(games, fullScreen, state);
                check(policy.Reason == reason && policy.IsMuted == (reason != NotificationMuteReason.None),
                    $"Mute switches act independently: games={games}, fullScreen={fullScreen}, state={state}");
            }
        }
        policy.Update(false, false, UserNotificationState.AcceptsNotifications);
        check(policy.Update(true, false, UserNotificationState.RunningDirect3DFullScreen)
            && policy.Reason == NotificationMuteReason.Game, "The game-only preference records only the game reason");
        check(policy.Update(true, false, UserNotificationState.Busy) && !policy.IsMuted,
            "The game-only preference leaves ordinary full-screen applications unmuted");
        check(policy.Update(false, true, UserNotificationState.Busy) && policy.Reason == NotificationMuteReason.FullScreen,
            "The full-screen-only preference mutes ordinary full-screen applications");
        check(!policy.Update(false, true, UserNotificationState.RunningDirect3DFullScreen) && policy.IsMuted,
            "The full-screen-only preference remains active when a game takes over the screen");
        check(policy.Update(true, true, UserNotificationState.RunningDirect3DFullScreen)
            && policy.Reason == (NotificationMuteReason.Game | NotificationMuteReason.FullScreen),
            "Both reasons are recorded when both preferences match");
        check(policy.Update(true, true, UserNotificationState.Busy) && policy.Reason == NotificationMuteReason.FullScreen,
            "Leaving the game updates the reason while the full-screen condition keeps notifications muted");
        check(policy.Update(true, true, UserNotificationState.AcceptsNotifications) && !policy.IsMuted,
            "Leaving the game restores new notification delivery");
        policy.Update(true, true, UserNotificationState.Busy);
        check(policy.Update(true, true, UserNotificationState.Unknown) && !policy.IsMuted,
            "A failed or unavailable system query cannot leave notifications permanently muted");

        var clock = new ManualTime();
        var router = new MessageRouter(clock);
        var hook = new IslandNotification(1, DateTimeOffset.Now, "微信", "测试会话", "测试消息",
            Source: NotificationSource.ClientHook, AppId: "wechat", EventId: "game/message/1");
        check(router.Receive(hook) is null && router.HasPending, "A client message initially awaits source merging");
        router.DiscardPending();
        clock.Advance(2);
        check(!router.HasPending && router.Flush().Count == 0,
            "Entering game mute discards a delayed popup without leaving a backlog");
        check(router.Receive(hook) is null && !router.HasPending,
            "Discarding muted messages retains client event deduplication");
        var system = hook with { Source = NotificationSource.SystemNotification, AppId = "Tencent.WeChat", EventId = null };
        check(router.Receive(system)?.ReplaceCurrent == true && !router.HasPending,
            "A delayed system copy of a muted client message only attempts enrichment, never a fresh popup");
        router.DiscardPending();
        check(router.Receive(hook) is null,
            "Repeated mute polling retains system history and suppresses another copy");

        var fresh = hook with { Id = 2, Body = "退出游戏后的新消息", EventId = "game/message/2" };
        router.Receive(fresh);
        clock.Advance(1.5);
        check(router.Flush().Single().Notification == fresh,
            "Discarding muted messages still allows a new message after the game ends");

        router.Clear();
        var automation = hook with { Source = NotificationSource.ClientAutomation, EventId = "game/automation/1" };
        router.Receive(automation);
        router.DiscardPending();
        clock.Advance(0.3);
        check(router.Flush().Count == 0 && router.Receive(automation) is null,
            "Automation messages are discarded without losing their event history");
        var settings = new AppSettings();
        foreach (var source in Enum.GetValues<NotificationSource>())
            check(settings.Allows(hook with { Source = source }), "Game mute keeps the message source enabled: " + source);
    }
}
