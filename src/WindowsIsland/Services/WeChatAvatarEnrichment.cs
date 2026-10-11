namespace WindowsIsland.Services;

internal sealed class WeChatAvatarEnrichment
{
    private sealed record Pending(string Key, IslandNotification Notification, DateTimeOffset Expires);
    private readonly List<Pending> _pending = [];

    public void Remember(IslandNotification notification, IReadOnlyList<WeChatPreview> previews, DateTimeOffset now)
    {
        if (notification.SenderAvatar is { Length: > 0 } || notification.EventId is null) return;
        var matches = previews.Where(item => item.Conversation == notification.Title).Take(2).ToArray();
        if (matches.Length != 1 || matches[0].Muted) return;
        _pending.RemoveAll(item => item.Expires <= now);
        if (_pending.Count >= 32) _pending.RemoveAt(0);
        _pending.Add(new(matches[0].Key, notification, now.AddSeconds(5)));
    }

    public IReadOnlyList<IslandNotification> Update(IReadOnlyList<WeChatPreview> previews, DateTimeOffset now)
    {
        var result = new List<IslandNotification>();
        foreach (var pending in _pending.ToArray())
        {
            if (pending.Expires <= now) { _pending.Remove(pending); continue; }
            var matches = previews.Where(item => item.Key == pending.Key).Take(2).ToArray();
            if (matches.Length == 0) continue;
            if (matches.Length != 1 || matches[0].Conversation != pending.Notification.Title || matches[0].Muted)
            { _pending.Remove(pending); continue; }
            if (matches[0].Avatar is not { Length: > 0 } avatar) continue;
            _pending.Remove(pending);
            result.Add(pending.Notification with { SenderAvatar = avatar, IsMetadataUpdate = true });
        }
        return result;
    }
}
