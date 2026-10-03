using System.Text.RegularExpressions;

namespace WindowsIsland.Services;

internal enum WeChatDirection { Unknown, Incoming, Outgoing }
internal sealed record WeChatPreview(string Key, string Conversation, string Body, int Unread, string Stamp, bool Muted = false);
internal sealed record WeChatMessage(string Key, string Sender, string Body, WeChatDirection Direction);
internal sealed record WeChatSnapshot(string Conversation, IReadOnlyList<WeChatPreview> Previews,
    IReadOnlyList<WeChatMessage> Messages, bool HasSessions, bool HasMessages, bool? ConversationMuted = null);
internal sealed record WeChatArrival(string Conversation, string Body);

internal static partial class WeChatText
{
    public static bool IdMatches(string id, string part) => id.Split('.').Contains(part, StringComparer.OrdinalIgnoreCase);

    public static WeChatPreview? ParsePreview(string id, string name, IReadOnlyList<string> labels)
    {
        var marker = id.LastIndexOf("session_item_", StringComparison.Ordinal);
        var conversation = marker >= 0 ? id[(marker + 13)..].Trim() : "";
        var lines = name.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var parts = (labels.Count >= 2 ? labels : lines).ToArray();
        if (conversation.Length == 0) conversation = parts.FirstOrDefault(text => !IsMeta(text)) ?? "";
        if (conversation.Length == 0) return null;
        var stampIndex = Array.FindLastIndex(parts, text => TimePattern().IsMatch(text));
        var stamp = stampIndex < 0 ? "" : parts[stampIndex];
        var bodyStart = Math.Max(0, Array.IndexOf(parts, conversation) + 1);
        while (bodyStart < stampIndex - 1 && IsMeta(parts[bodyStart])) bodyStart++;
        var structured = marker >= 0 && stampIndex > bodyStart;
        var body = structured ? string.Join('\n', parts[bodyStart..stampIndex])
            : parts.LastOrDefault(text => text != conversation && !IsMeta(text)) ?? "";
        var metadata = parts.Where((text, index) => structured ? index < bodyStart || index > stampIndex : text != body).ToArray();
        var unreadMatch = metadata.Select(text => UnreadPattern().Match(text)).FirstOrDefault(match => match.Success);
        var unread = unreadMatch is not null && int.TryParse(unreadMatch.Groups["count"].Value, out var count) ? count
            : metadata.Any(text => text is "未读" or "有未读消息" or "Unread") ? 1 : 0;
        if (structured && BracketedUnreadPrefix().Match(body) is { Success: true } prefix && prefix.Length < body.Length)
        {
            if (unreadMatch is null && int.TryParse(prefix.Groups[1].Value, out var inlineCount)) unread = inlineCount;
            body = body[prefix.Length..].TrimStart();
        }
        if (body.Length == 0 && name.StartsWith(conversation, StringComparison.Ordinal))
        {
            body = name[conversation.Length..].Trim(' ', '\r', '\n', ':', '：', ',', '，');
            body = UnreadPattern().Replace(body, "").Trim();
            if (stamp.Length > 0) body = body.Replace(stamp, "", StringComparison.Ordinal).Trim();
        }
        if (body.Length > 2048) body = body[..2048];
        return new(id.Length == 0 ? conversation : id, conversation, body, unread, stamp, metadata.Any(IsMuteLabel));
    }

    public static bool IsDraft(string text) => text.StartsWith("[草稿]", StringComparison.Ordinal)
        || text.StartsWith("草稿：", StringComparison.Ordinal) || text.StartsWith("[Draft]", StringComparison.OrdinalIgnoreCase);

    public static bool MatchesPreview(string preview, string body)
    {
        preview = preview.Trim().TrimEnd('…', '.').Replace('：', ':').Replace(": ", ":", StringComparison.Ordinal);
        body = body.Trim().Replace('：', ':').Replace(": ", ":", StringComparison.Ordinal);
        return preview.Length > 0 && (preview == body || preview.EndsWith(body, StringComparison.Ordinal)
            || preview.Length >= 4 && body.StartsWith(preview, StringComparison.Ordinal));
    }

    public static bool Recent(string stamp, DateTimeOffset now)
    {
        if (stamp is "现在" or "刚刚" or "Now" or "Just now") return true;
        if (!TimeOnly.TryParseExact(stamp, "HH:mm", out var clock)) return false;
        var local = now.LocalDateTime;
        var at = local.Date.Add(clock.ToTimeSpan());
        if (at - local > TimeSpan.FromMinutes(1)) at = at.AddDays(-1);
        return local - at is var age && age >= TimeSpan.FromMinutes(-1) && age <= TimeSpan.FromMinutes(2);
    }

    private static bool IsMuteLabel(string text) => text is "消息免打扰" or "已免打扰" or "訊息免打擾" or "Muted" or "Mute notifications";
    private static bool IsMeta(string text) => UnreadPattern().IsMatch(text) || TimePattern().IsMatch(text) || IsMuteLabel(text)
        || text is "置顶" or "已置顶" or "未读" or "有未读消息" or "Unread";

    [GeneratedRegex(@"^(?:(?<count>\d+)\+?\s*(?:条(?:新|未读)?消息|条?未读(?:消息)?|(?:new|unread)\s+messages?)|\[(?<count>\d+)\+?\s*(?:条(?:新|未读)?消息|条?未读(?:消息)?|(?:new|unread)\s+messages?|条)?\])$", RegexOptions.IgnoreCase)]
    private static partial Regex UnreadPattern();
    [GeneratedRegex(@"^\[(\d+)\+?(?:条)?\]\s*")]
    private static partial Regex BracketedUnreadPrefix();
    [GeneratedRegex(@"^(?:(?:(?:今天|昨天|前天|星期[一二三四五六日天]|周[一二三四五六日天])\s+)?\d{1,2}:\d{2}|\d{1,2}[/-]\d{1,2}|\d{4}[/-]\d{1,2}[/-]\d{1,2}|昨天|前天|星期[一二三四五六日天]|周[一二三四五六日天]|现在|刚刚|Yesterday|Now|Just now)$", RegexOptions.IgnoreCase)]
    private static partial Regex TimePattern();
}

internal sealed class WeChatTracker
{
    private readonly Dictionary<string, WeChatPreview> _previews = [];
    private readonly Dictionary<string, HashSet<string>> _messages = [];
    private bool _initialized;

    public IReadOnlyList<WeChatArrival> Update(WeChatSnapshot snapshot, DateTimeOffset now)
    {
        var arrivals = new List<WeChatArrival>();
        var fresh = new List<WeChatMessage>();
        var currentMute = snapshot.ConversationMuted
            ?? snapshot.Previews.FirstOrDefault(item => item.Conversation == snapshot.Conversation)?.Muted
            ?? _previews.Values.FirstOrDefault(item => item.Conversation == snapshot.Conversation)?.Muted;
        if (snapshot.HasMessages && snapshot.Conversation.Length > 0)
        {
            if (_messages.TryGetValue(snapshot.Conversation, out var known))
            {
                var anchor = -1;
                for (var index = 0; index < snapshot.Messages.Count; index++)
                    if (known.Contains(snapshot.Messages[index].Key)) anchor = index;
                if (anchor >= 0 || known.Count == 0)
                    fresh.AddRange(snapshot.Messages.Skip(anchor + 1).Where(message => !known.Contains(message.Key)));
            }
            _messages[snapshot.Conversation] = snapshot.Messages.Select(message => message.Key).ToHashSet();
            foreach (var message in fresh.Where(message => currentMute is false && message.Direction == WeChatDirection.Incoming))
                arrivals.Add(new(snapshot.Conversation, message.Sender.Length == 0 || message.Sender == snapshot.Conversation
                    ? message.Body : message.Sender + "：" + message.Body));
        }

        foreach (var preview in snapshot.Previews)
        {
            var changed = _previews.TryGetValue(preview.Key, out var previous)
                ? preview.Unread > previous.Unread || preview.Body != previous.Body && preview.Unread > 0
                : _initialized && snapshot.Previews[0] == preview && WeChatText.Recent(preview.Stamp, now);
            _previews[preview.Key] = preview;
            if (!_initialized || !changed || preview.Muted || preview.Unread <= 0 || preview.Body.Length == 0 || WeChatText.IsDraft(preview.Body)) continue;
            if (arrivals.Any(arrival => arrival.Conversation == preview.Conversation && WeChatText.MatchesPreview(preview.Body, arrival.Body))) continue;
            var message = snapshot.Conversation == preview.Conversation
                ? fresh.LastOrDefault(item => item.Direction != WeChatDirection.Outgoing && WeChatText.MatchesPreview(preview.Body, item.Body)) : null;
            arrivals.Add(new(preview.Conversation, message?.Body ?? preview.Body));
        }
        if (snapshot.HasSessions) _initialized = true;
        if (_previews.Count > 1024)
            foreach (var key in _previews.Keys.Except(snapshot.Previews.Select(item => item.Key)).Take(_previews.Count - 512).ToArray()) _previews.Remove(key);
        if (_messages.Count > 128)
            foreach (var key in _messages.Keys.Where(key => key != snapshot.Conversation).Take(_messages.Count - 64).ToArray()) _messages.Remove(key);
        return arrivals;
    }

    public void Reset() { _previews.Clear(); _messages.Clear(); _initialized = false; }
}

internal sealed class WeChatConversationRegistry(TimeProvider? time = null)
{
    private sealed record Entry(int ProcessId, string Conversation, bool Muted, long ObservedAt);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private Entry[] _entries = [];
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(10);

    public void Update(int processId, IReadOnlyList<WeChatPreview> previews)
    {
        var latest = Volatile.Read(ref _entries).Where(entry => _time.GetElapsedTime(entry.ObservedAt) <= Lifetime)
            .ToDictionary(entry => (entry.ProcessId, entry.Conversation));
        foreach (var group in previews.GroupBy(preview => preview.Conversation))
            latest[(processId, group.Key)] = new(processId, group.Key, group.Any(preview => preview.Muted), _time.GetTimestamp());
        Volatile.Write(ref _entries, latest.Values.TakeLast(1024).ToArray());
    }

    public bool? IsMuted(string conversation, int? processId = null)
    {
        var matches = Volatile.Read(ref _entries).Where(entry => entry.Conversation == conversation
            && (processId is null || entry.ProcessId == processId) && _time.GetElapsedTime(entry.ObservedAt) <= Lifetime).ToArray();
        return matches.Length == 0 ? null : matches.Any(entry => entry.Muted);
    }

    public void Remove(int processId) => Volatile.Write(ref _entries,
        Volatile.Read(ref _entries).Where(entry => entry.ProcessId != processId).ToArray());
    public void Clear() => Volatile.Write(ref _entries, []);
}
