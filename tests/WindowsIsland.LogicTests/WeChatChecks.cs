using WindowsIsland.Services;

internal static class WeChatChecks
{
    public static void Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.Now;
        var stamp = now.ToString("HH:mm");
        var preview = WeChatText.ParsePreview("session_item_示例群", $"示例群\n已置顶\n小林: 下午见\n{stamp}\n消息免打扰\n2条未读消息", [])!;
        check(preview.Conversation == "示例群" && preview.Body == "小林: 下午见" && preview.Unread == 2 && preview.Stamp == stamp,
            "WeChat 4 session text excludes pin, mute, time and unread metadata");
        check(WeChatText.ParsePreview("", "小林\n明天见", ["小林", "昨天", "明天见"]) is { Conversation: "小林", Body: "明天见", Stamp: "昨天" },
            "Older WeChat session text controls remain readable");
        check(WeChatText.ParsePreview("session_item_小林", "小林\n昨天 13:00\n9条未读消息\n你好", []) is { Unread: 9, Body: "你好" },
            "Date and time labels do not leak into message previews");
        check(WeChatText.ParsePreview("session_item_小林", "小林\n3未读\n你好", []) is { Unread: 3, Body: "你好" }, "Alternate unread labels are recognized");
        check(WeChatText.ParsePreview("session_item_小林", "小林\n有未读消息\n你好", []) is { Unread: 1, Body: "你好" }, "Muted unread dots are recognized");
        check(WeChatText.ParsePreview("", "", []) is null, "Empty session peers are ignored");
        check(WeChatText.ParsePreview("session_item_小林", $"小林\n已置顶\n今天有9条未读消息\n{stamp}\n0条未读消息", []) is { Body: "今天有9条未读消息", Unread: 0 },
            "Unread-looking text inside a message does not create an unread count");
        check(WeChatText.ParsePreview("session_item_小林", $"小林\n已置顶\n3条未读消息\n{stamp}\n0条未读消息", []) is { Body: "3条未读消息", Unread: 0 },
            "An exact unread-looking message remains message content");
        check(WeChatText.ParsePreview("session_item_小林", $"小林\n第一行\n第二行\n{stamp}\n2条未读消息", []) is { Body: "第一行\n第二行", Unread: 2 },
            "Multiple preview lines keep their order and full content");
        check(WeChatText.ParsePreview("session_item_小林", $"小林\n已置顶\n[9条]\n1\n{stamp}", []) is { Body: "1", Unread: 9 },
            "The live WeChat bracketed unread label preserves a numeric test message");
        check(WeChatText.ParsePreview("session_item_小林", $"小林\n已置顶\n[8]\n你好\n{stamp}", []) is { Body: "你好", Unread: 8 },
            "Bracketed counts without a unit remain unread metadata");
        check(WeChatText.ParsePreview("session_item_示例群", $"示例群\n已置顶\n[4条] 小林: 下午见\n{stamp}", []) is { Body: "小林: 下午见", Unread: 4 },
            "Inline unread counts are removed from the displayed group preview");
        check(WeChatText.ParsePreview("session_item_小林", $"小林\n已置顶\n[3条]\n{stamp}\n0条未读消息", []) is { Body: "[3条]", Unread: 0 },
            "A bracketed number used as the entire message is not mistaken for unread metadata");
        check(WeChatText.IdMatches("MainView.detail.chat_message_list", "chat_message_list")
            && !WeChatText.IdMatches("chat_message_list_toolbar", "chat_message_list"), "Dotted automation identifiers match exact segments");
        check(WeChatText.MatchesPreview("小林: 下午见", "下午见") && WeChatText.MatchesPreview("小林: 下午见", "小林：下午见"),
            "Group previews match sender-prefixed messages");
        check(WeChatText.MatchesPreview("这是一个较长的预览…", "这是一个较长的预览，后面还有正文")
            && !WeChatText.MatchesPreview("你好", "你好世界"), "Truncated previews match without conflating short messages");
        check(WeChatText.Recent(stamp, now) && !WeChatText.Recent("昨天", now), "Only recent new sessions can generate arrivals");

        var tracker = new WeChatTracker();
        var friend = new WeChatPreview("friend", "小林", "历史消息", 4, "昨天");
        WeChatSnapshot Sessions(params WeChatPreview[] items) => new("", items, [], true, false);
        check(tracker.Update(Sessions(friend), now).Count == 0, "Existing unread messages stay silent when listening begins");
        check(tracker.Update(Sessions(friend), now).Count == 0, "Repeated session snapshots stay silent");
        friend = friend with { Body = "新消息", Unread = 5, Stamp = stamp };
        check(tracker.Update(Sessions(friend), now).Single() == new WeChatArrival("小林", "新消息"), "New unread preview changes produce an arrival");
        friend = friend with { Unread = 6 };
        check(tracker.Update(Sessions(friend), now).Count == 1, "Identical text sent twice remains two distinct arrivals");
        friend = friend with { Unread = 0 };
        check(tracker.Update(Sessions(friend), now).Count == 0, "Opening a conversation does not replay unread messages");
        friend = friend with { Body = "我发出的消息" };
        check(tracker.Update(Sessions(friend), now).Count == 0, "Outgoing previews without unread messages stay silent");
        friend = friend with { Body = "[草稿] 尚未发送", Unread = 1 };
        check(tracker.Update(Sessions(friend), now).Count == 0, "Draft previews never create notifications");
        var added = new WeChatPreview("new", "新会话", "你好", 1, stamp);
        check(tracker.Update(Sessions(added, friend), now).Single().Conversation == "新会话", "A newly arrived recent conversation is detected");
        var older = new WeChatPreview("older", "旧会话", "较早消息", 9, "昨天");
        check(tracker.Update(Sessions(older, added), now).Count == 0, "Scrolling to old unread sessions does not replay their messages");

        var messages = new WeChatTracker();
        WeChatSnapshot Chat(params WeChatMessage[] items) => new("小林", [], items, false, true, false);
        var first = new WeChatMessage("1", "小林", "此前的消息", WeChatDirection.Incoming);
        check(messages.Update(Chat(first), now).Count == 0, "The currently open chat starts with a silent history baseline");
        var incoming = new WeChatMessage("2", "小林", "新消息", WeChatDirection.Incoming);
        check(messages.Update(Chat(first, incoming), now).Single().Body == "新消息", "New incoming rows are detected by runtime identity");
        check(messages.Update(Chat(first, incoming), now).Count == 0, "Reading a message row again never creates a duplicate");
        var outgoing = new WeChatMessage("3", "我", "回复", WeChatDirection.Outgoing);
        check(messages.Update(Chat(first, incoming, outgoing), now).Count == 0, "Explicit outgoing rows do not notify");
        var unknown = new WeChatMessage("4", "", "方向未知", WeChatDirection.Unknown);
        check(messages.Update(Chat(first, incoming, outgoing, unknown), now).Count == 0, "Unknown direction alone does not classify a sent message as received");
        check(messages.Update(Chat(new WeChatMessage("history", "小林", "更早的消息", WeChatDirection.Incoming)), now).Count == 0,
            "Replacing the visible history without a known anchor does not replay it");
        var otherChat = new WeChatSnapshot("另一个会话", [], [incoming], false, true);
        check(messages.Update(otherChat, now).Count == 0, "Switching conversations creates a separate silent baseline");

        var combined = new WeChatTracker();
        var basePreview = new WeChatPreview("group", "示例群", "旧消息", 0, stamp);
        WeChatSnapshot Group(WeChatPreview session, params WeChatMessage[] items) => new("示例群", [session], items, true, true);
        combined.Update(Group(basePreview, first), now);
        var groupMessage = incoming with { Sender = "小林", Body = "下午见" };
        check(combined.Update(Group(basePreview with { Body = "小林: 下午见", Unread = 1 }, first, groupMessage), now).Count == 1,
            "Session and chat events merge into a single group notification");
        var fullBody = "这是一条较长的完整正文，用于检查预览补全";
        var unknownMessage = unknown with { Body = fullBody };
        check(combined.Update(Group(basePreview with { Body = fullBody[..10] + "…", Unread = 2 }, first, groupMessage, unknownMessage), now).Single().Body == fullBody,
            "Unread preview changes can safely enrich an otherwise unknown-direction row");
        combined.Reset();
        check(combined.Update(Group(basePreview with { Unread = 10 }, groupMessage), now).Count == 0,
            "Reconnecting or enabling listening does not replay accumulated messages");

        byte[] personalAvatar = [1, 2, 3], groupAvatar = [4, 5, 6];
        var portraits = new WeChatTracker();
        var personalPreview = new WeChatPreview("personal", "小林", "旧消息", 0, stamp, Avatar: personalAvatar);
        var groupPreview = basePreview with { Avatar = groupAvatar };
        portraits.Update(Sessions(personalPreview, groupPreview), now);
        check(portraits.Update(Sessions(personalPreview with { Body = "你好", Unread = 1 }, groupPreview), now).Single().Avatar == personalAvatar,
            "Personal conversation arrivals retain their own portrait");
        check(portraits.Update(Sessions(personalPreview with { Body = "你好", Unread = 1 }, groupPreview with { Body = "群聊消息", Unread = 1 }), now).Single().Avatar == groupAvatar,
            "Group conversation arrivals do not inherit the personal portrait");
        portraits.Reset();
        portraits.Update(Group(groupPreview, first), now);
        check(portraits.Update(Group(groupPreview with { Body = "小林: 下午见", Unread = 1 }, first, groupMessage), now).Single().Avatar == groupAvatar,
            "Merged group chat rows and previews preserve the conversation portrait");
        portraits.Reset();
        portraits.Update(Sessions(personalPreview), now);
        check(portraits.Update(Sessions(personalPreview with { Avatar = groupAvatar }), now).Count == 0,
            "Refreshing an avatar alone does not replay the last message");
        check(portraits.Update(Sessions(personalPreview with { Body = "头像暂时不可读", Unread = 1, Avatar = null }), now).Single().Avatar == groupAvatar,
            "A temporarily unavailable avatar falls back to the last portrait for the same conversation");

        var muted = WeChatText.ParsePreview("session_item_示例群", $"示例群\n已置顶\n[9条]\n新消息\n{stamp}\n消息免打扰", [])!;
        check(muted.Muted && muted.Body == "新消息" && muted.Unread == 9, "Mute metadata is retained independently of body and unread count");
        check(WeChatText.ParsePreview("session_item_小林", $"小林\n已置顶\n消息免打扰\n{stamp}", []) is { Body: "消息免打扰", Muted: false },
            "Message text mentioning mute settings does not silence an unmuted conversation");
        var mutedTracker = new WeChatTracker();
        mutedTracker.Update(Group(muted, first), now);
        check(mutedTracker.Update(Group(muted with { Unread = 10 }, first, incoming), now).Count == 0,
            "Muted conversations suppress both message rows and unread previews");
        check(mutedTracker.Update(Group(muted with { Muted = false, Unread = 10 }, first, incoming), now).Count == 0,
            "Turning mute off does not replay accumulated messages");
        check(mutedTracker.Update(Group(muted with { Muted = false, Unread = 11, Body = "下一条" }, first, incoming), now).Count == 1,
            "New unread messages can arrive after mute is disabled");
        var unknownMute = new WeChatTracker();
        unknownMute.Update(Chat(first) with { ConversationMuted = null }, now);
        check(unknownMute.Update(Chat(first, incoming) with { ConversationMuted = null }, now).Count == 0,
            "An incoming row with an unknown mute state does not produce a notification");

        var clock = new ManualTime();
        var registry = new WeChatConversationRegistry(clock);
        check(registry.IsMuted("示例群") is null, "Unobserved conversations have an unknown mute state");
        registry.Update(10, [muted]);
        check(registry.IsMuted("示例群", 10) is true, "Known muted conversations remain filtered across message sources");
        registry.Update(20, [muted with { Muted = false }]);
        check(registry.IsMuted("示例群", 20) is false && registry.IsMuted("示例群") is true,
            "Multiple clients retain separate mute states and ambiguous sources stay muted");
        registry.Update(10, [muted, muted with { Key = "duplicate", Muted = false }]);
        check(registry.IsMuted("示例群", 10) is true,
            "Conversations sharing a display name cannot overwrite a known muted state");
        registry.Remove(10);
        check(registry.IsMuted("示例群") is false, "Closing a client removes its conversation state");
        clock.Advance(11);
        check(registry.IsMuted("示例群") is null, "Stale mute information is not treated as permission to notify");
        registry.Clear();
        check(registry.IsMuted("示例群", 20) is null, "Listener cleanup releases conversation state");
    }
}
