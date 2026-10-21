namespace WindowsIsland.Services;

internal enum NotificationAccess { NeedsRegistration, Waiting, Allowed, Denied, Unavailable }

internal enum NotificationSource { SystemNotification, ClientHook, Power, ClientAutomation }

internal sealed record IslandNotification(uint Id, DateTimeOffset CreatedAt, string AppName, string Title, string Body,
    byte[]? AppIcon = null, NotificationSource Source = NotificationSource.SystemNotification,
    string? AppId = null, string? EventId = null, string? Symbol = null, byte[]? SenderAvatar = null, int? OriginProcessId = null,
    bool IsMetadataUpdate = false, bool AllowActivation = true);

internal sealed class NotificationTracker
{
    private HashSet<(uint, DateTimeOffset)> _known = [];
    private bool _initialized;

    public IReadOnlyList<IslandNotification> Update(IReadOnlyList<IslandNotification> snapshot)
    {
        var fresh = _initialized
            ? snapshot.Where(item => !_known.Contains((item.Id, item.CreatedAt))).OrderBy(item => item.CreatedAt).ToArray()
            : [];
        _known = snapshot.Select(item => (item.Id, item.CreatedAt)).ToHashSet();
        _initialized = true;
        return fresh;
    }

    public void Reset() { _known.Clear(); _initialized = false; }
}
