using WindowsIsland.Contracts;

namespace WindowsIsland.Services;

internal enum IslandState { Minimal, Compact, Expanded }
internal enum IslandExpandedKind { None, Message, Pinned }

// 唯一的内容与截止时间所有者；由宿主串行投递事件，不依赖界面或计时器。
internal sealed class IslandScheduler(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long _shownAt;
    private bool _muted;
    private bool _pinnedOpen;
    public IslandNotification? Message { get; private set; }
    public PinnedContent? Pinned { get; private set; }
    public static TimeSpan DisplayDuration => TimeSpan.FromSeconds(5);
    public bool MessageIsExpired => Message is not null && _time.GetElapsedTime(_shownAt) >= DisplayDuration;
    public bool HasContent => Message is not null || Pinned is not null;
    public IslandState State => _muted ? IslandState.Minimal : Message is not null ? IslandState.Expanded
        : Pinned is not null ? _pinnedOpen ? IslandState.Expanded : IslandState.Compact : IslandState.Minimal;
    public IslandExpandedKind ExpandedKind => State != IslandState.Expanded ? IslandExpandedKind.None
        : Message is not null ? IslandExpandedKind.Message : IslandExpandedKind.Pinned;

    public void MessageArrived(IslandNotification message)
    {
        if (_muted) return;
        _pinnedOpen = false;
        Message = message;
        _shownAt = _time.GetTimestamp();
    }

    // 元数据补全及语言刷新不延长消息的显示时间。
    public void MessageUpdated(IslandNotification message)
    {
        if (Message is not null) Message = message;
    }

    public void MessageDismissed() => Message = null;
    public void MessageExpired() { if (MessageIsExpired) Message = null; }

    public void PinnedAdded(PinnedContent content)
    {
        if (content.ExpiresAt <= _time.GetUtcNow()) { PinnedRemoved(content.PluginId, content.Id); return; }
        if (!Matches(content.PluginId, content.Id)) _pinnedOpen = false;
        Pinned = content;
        if (!HasDetails(content)) _pinnedOpen = false;
    }

    public void PinnedUpdated(PinnedContent content)
    {
        if (!Matches(content.PluginId, content.Id)) return;
        Pinned = content.ExpiresAt <= _time.GetUtcNow() ? null : content;
        if (!HasDetails(Pinned)) _pinnedOpen = false;
    }

    public void PinnedRemoved(string pluginId, string? id = null)
    {
        if (Pinned?.PluginId == pluginId && (id is null || Pinned.Id == id)) { Pinned = null; _pinnedOpen = false; }
    }

    public void PinnedClicked()
    {
        if (State == IslandState.Compact && HasDetails(Pinned)) _pinnedOpen = true;
    }
    public void PanelDismissed() => _pinnedOpen = false;
    private static bool HasDetails(PinnedContent? content) => content is not null
        && (!string.IsNullOrWhiteSpace(content.Body) || content.Controls?.Count > 0 || !string.IsNullOrWhiteSpace(content.CustomPanelXaml));

    private bool Matches(string pluginId, string id) => Pinned?.PluginId == pluginId && Pinned.Id == id;

    public bool Tick()
    {
        var message = Message;
        var pinned = Pinned;
        MessageExpired();
        if (Pinned?.ExpiresAt <= _time.GetUtcNow()) { Pinned = null; _pinnedOpen = false; }
        return message != Message || pinned != Pinned;
    }

    public void SetMuted(bool muted)
    {
        _muted = muted;
        if (muted) { Message = null; _pinnedOpen = false; }
        Tick();
    }
}
