using System.Text;

namespace WindowsIsland.Services;

internal sealed record MessageDispatch(IslandNotification Notification, bool ReplaceCurrent = false);

internal sealed class MessageRouter(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly List<Entry> _pending = [];
    private readonly List<Entry> _recent = [];
    private readonly Dictionary<(NotificationSource Source, string? AppId, string EventId), long> _events = [];
    public static TimeSpan HookDelay => TimeSpan.FromMilliseconds(1400);
    public static TimeSpan AutomationDelay => TimeSpan.FromMilliseconds(250);
    public bool HasPending => _pending.Count > 0;
    private static readonly TimeSpan MatchWindow = TimeSpan.FromSeconds(8);
    private sealed record Entry(IslandNotification Notification, long Timestamp, bool MatchedClient = false);

    public MessageDispatch? Receive(IslandNotification notification)
    {
        Prune();
        if (notification.Source == NotificationSource.Power) return new(notification);
        if (IsClient(notification))
        {
            var systemIndex = _recent.FindLastIndex(item => item.Notification.Source == NotificationSource.SystemNotification && Matches(item.Notification, notification));
            if (systemIndex >= 0)
            {
                var system = _recent[systemIndex];
                var enriched = PreserveImages(system.Notification, notification);
                if (ReferenceEquals(enriched.AppIcon, system.Notification.AppIcon)
                    && ReferenceEquals(enriched.SenderAvatar, system.Notification.SenderAvatar)) return null;
                _recent[systemIndex] = system with { Notification = enriched };
                return new(enriched, ReplaceCurrent: true);
            }
            if (_pending.Concat(_recent).Any(item => SameEvent(item.Notification, notification))) return null;
            if (notification.EventId is { } eventId)
            {
                var key = (notification.Source, notification.AppId, eventId);
                if (_events.ContainsKey(key)) return null;
                _events[key] = _time.GetTimestamp();
                if (_events.Count > 256) _events.Remove(_events.Keys.First());
            }
            var pendingIndex = _pending.FindIndex(item => OtherClient(item, notification));
            if (pendingIndex >= 0)
            {
                var pending = _pending[pendingIndex];
                _pending[pendingIndex] = pending with { Notification = PreferAutomation(pending.Notification, notification), MatchedClient = true };
                return null;
            }
            var recentIndex = _recent.FindIndex(item => OtherClient(item, notification));
            if (recentIndex >= 0)
            {
                var recent = _recent[recentIndex];
                var enriched = PreferAutomation(recent.Notification, notification);
                _recent[recentIndex] = recent with { Notification = enriched, MatchedClient = true };
                return enriched == recent.Notification ? null : new(enriched, ReplaceCurrent: true);
            }
            if (_pending.Count >= 64) _pending.RemoveAt(0);
            _pending.Add(new(notification, _time.GetTimestamp()));
            return null;
        }

        var pendingHook = _pending.LastOrDefault(item => Matches(item.Notification, notification));
        _pending.RemoveAll(item => Matches(item.Notification, notification));
        var hook = _recent.LastOrDefault(item => IsClient(item.Notification) && Matches(item.Notification, notification));
        _recent.RemoveAll(item => IsClient(item.Notification) && Matches(item.Notification, notification));
        if ((hook ?? pendingHook) is { } matchingHook) notification = PreserveImages(notification, matchingHook.Notification);
        Remember(notification);
        return new(notification, ReplaceCurrent: hook is not null);
    }

    private static bool IsClient(IslandNotification notification) => notification.Source is NotificationSource.ClientHook or NotificationSource.ClientAutomation;
    private static bool OtherClient(Entry entry, IslandNotification notification) => !entry.MatchedClient
        && IsClient(entry.Notification) && entry.Notification.Source != notification.Source && Matches(entry.Notification, notification);
    private static IslandNotification PreferAutomation(IslandNotification first, IslandNotification second) =>
        first.Source == NotificationSource.ClientAutomation ? PreserveImages(first, second) : PreserveImages(second, first);

    private static IslandNotification PreserveImages(IslandNotification primary, IslandNotification fallback) => primary with
    {
        AppIcon = primary.AppIcon is { Length: > 0 } || fallback.AppIcon is not { Length: > 0 } ? primary.AppIcon : fallback.AppIcon,
        SenderAvatar = primary.SenderAvatar is { Length: > 0 } || fallback.SenderAvatar is not { Length: > 0 } ? primary.SenderAvatar : fallback.SenderAvatar,
        OriginProcessId = primary.OriginProcessId ?? fallback.OriginProcessId
    };

    public IReadOnlyList<MessageDispatch> Flush()
    {
        Prune();
        var ready = _pending.Where(item => _time.GetElapsedTime(item.Timestamp) >=
            (item.Notification.Source == NotificationSource.ClientAutomation ? AutomationDelay : HookDelay)).ToArray();
        var result = new List<MessageDispatch>();
        foreach (var item in ready)
        {
            _pending.Remove(item);
            if (_recent.Any(system => system.Notification.Source == NotificationSource.SystemNotification && Matches(system.Notification, item.Notification))) continue;
            Remember(item.Notification, item.MatchedClient);
            result.Add(new(item.Notification));
        }
        return result;
    }

    public void Clear() { _pending.Clear(); _recent.Clear(); _events.Clear(); }
    public void DiscardPending()
    {
        foreach (var item in _pending) Remember(item.Notification, item.MatchedClient);
        _pending.Clear();
    }
    public void ClearSystemHistory() => _recent.RemoveAll(item => item.Notification.Source == NotificationSource.SystemNotification);

    private void Remember(IslandNotification notification, bool matchedClient = false)
    {
        _recent.Add(new(notification, _time.GetTimestamp(), matchedClient));
        if (_recent.Count > 128) _recent.RemoveAt(0);
    }

    private void Prune()
    {
        _recent.RemoveAll(item => _time.GetElapsedTime(item.Timestamp) > MatchWindow);
        foreach (var key in _events.Where(item => _time.GetElapsedTime(item.Value) > MatchWindow).Select(item => item.Key).ToArray()) _events.Remove(key);
    }

    private static bool SameEvent(IslandNotification left, IslandNotification right) =>
        left.Source == right.Source && left.AppId == right.AppId && left.EventId is not null && left.EventId == right.EventId;

    internal static bool Matches(IslandNotification left, IslandNotification right)
    {
        var app = MessengerIdentity.FromNotification(left);
        if (app is null || app != MessengerIdentity.FromNotification(right)) return false;
        if ((left.CreatedAt - right.CreatedAt).Duration() > MatchWindow) return false;
        var leftBody = Normalize(left.Body);
        var rightBody = Normalize(right.Body);
        if (leftBody.Length == 0 || rightBody.Length == 0) return false;
        var bodiesMatch = leftBody == rightBody || Math.Min(leftBody.Length, rightBody.Length) >= 8
            && (leftBody.StartsWith(rightBody, StringComparison.Ordinal) || rightBody.StartsWith(leftBody, StringComparison.Ordinal));
        if (!bodiesMatch) return false;
        var leftTitle = Normalize(left.Title);
        var rightTitle = Normalize(right.Title);
        return leftTitle == rightTitle || GenericTitle(leftTitle, left) || GenericTitle(rightTitle, right)
            || leftTitle.StartsWith(rightTitle + ":", StringComparison.Ordinal)
            || rightTitle.StartsWith(leftTitle + ":", StringComparison.Ordinal);
    }

    private static bool GenericTitle(string title, IslandNotification notification) =>
        title.Length == 0 || title == Normalize(notification.AppName);

    private static string Normalize(string text) => string.Concat(text.Normalize(NormalizationForm.FormKC)
        .Where(character => !char.IsWhiteSpace(character) && character is not '\u200B' and not '\u200E' and not '\u200F'))
        .TrimEnd('…', '.').ToUpperInvariant();
}

internal static class MessengerIdentity
{
    public static string? FromProcess(string name) => name.ToLowerInvariant() switch
    {
        "wechat" or "weixin" or "wechatappex" => "wechat",
        "telegram" => "telegram",
        _ => null
    };

    public static string? FromNotification(IslandNotification notification)
    {
        if (notification.AppId is "wechat" or "qq" or "telegram") return notification.AppId;
        var name = (notification.AppName + " " + notification.AppId).ToLowerInvariant();
        if (name.Contains("微信") || name.Contains("wechat") || name.Contains("weixin")) return "wechat";
        if (name.Contains("telegram")) return "telegram";
        if (name.Contains("qq")) return "qq";
        return null;
    }

    public static string DisplayName(string identity) => identity switch { "wechat" => "微信", "qq" => "QQ", _ => "Telegram" };
}
