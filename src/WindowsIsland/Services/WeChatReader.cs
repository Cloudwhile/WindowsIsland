using System.Windows.Automation;

namespace WindowsIsland.Services;

internal sealed class WeChatReader(AutomationElement root, nint nativeWindow = 0) : IDisposable
{
    private readonly WeChatAvatarReader _avatars = new(root, nativeWindow);
    private AutomationElement? _sessions, _messages, _input, _title;
    private DateTimeOffset _discovered;

    public WeChatSnapshot Read()
    {
        _avatars.BeginRead();
        try
        {
            if (_sessions is null && _messages is null || DateTimeOffset.UtcNow - _discovered > TimeSpan.FromSeconds(3)) Discover();
            var previews = new List<WeChatPreview>();
            var messages = new List<WeChatMessage>();
            var conversation = Name(_input);
            if (conversation.Length == 0) conversation = Name(_title);
            if (conversation.Length == 0 && root.Current.ClassName == "ChatWnd") conversation = root.Current.Name.Trim();
            var hasSessions = ReadPreviews(previews);
            var hasMessages = ReadMessages(messages);
            return new(conversation, previews, messages, hasSessions, hasMessages,
                previews.FirstOrDefault(preview => preview.Conversation == conversation)?.Muted);
        }
        finally { _avatars.EndRead(); }
    }

    private void Discover()
    {
        _discovered = DateTimeOffset.UtcNow;
        var condition = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        var rootClass = root.Current.ClassName;
        var cache = new CacheRequest();
        cache.Add(AutomationElement.NameProperty); cache.Add(AutomationElement.AutomationIdProperty);
        cache.Add(AutomationElement.ControlTypeProperty); cache.Add(AutomationElement.ClassNameProperty);
        using (cache.Activate())
        {
            foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, condition))
            {
                var info = element.Cached;
                if (info.ControlType == ControlType.List)
                {
                    if (WeChatText.IdMatches(info.AutomationId, "session_list") || info.Name is "会话" or "聊天" or "Chats") _sessions = element;
                    else if (WeChatText.IdMatches(info.AutomationId, "chat_message_list") || info.Name is "消息" or "Messages") _messages = element;
                }
                else if (WeChatText.IdMatches(info.AutomationId, "chat_input_field") || info.ControlType == ControlType.Edit
                    && info.Name.Length > 0 && info.Name is not ("搜索" or "Search") && rootClass == "WeChatMainWndForPC") _input = element;
                else if (WeChatText.IdMatches(info.AutomationId, "current_chat_name_label")) _title = element;
            }
        }
    }

    private bool ReadPreviews(List<WeChatPreview> result)
    {
        if (_sessions is null) return false;
        try
        {
            var viewport = _sessions.Current.BoundingRectangle;
            var cache = new CacheRequest();
            cache.Add(AutomationElement.NameProperty); cache.Add(AutomationElement.AutomationIdProperty);
            cache.Add(AutomationElement.ClassNameProperty);
            using (cache.Activate())
            {
                var rows = _sessions.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                foreach (AutomationElement row in rows)
                {
                    if (result.Count >= 80) break;
                    var info = row.Cached;
                    if (info.ClassName.Contains("Fold", StringComparison.Ordinal)) continue;
                    var labels = new List<string>();
                    // Older clients expose title, time and preview as separate text controls.
                    if (!info.ClassName.StartsWith("mmui::", StringComparison.Ordinal))
                        foreach (AutomationElement label in row.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)))
                            if (labels.Count < 8 && label.Current.Name.Trim() is { Length: > 0 } text) labels.Add(text);
                    var preview = WeChatText.ParsePreview(info.AutomationId, info.Name, labels);
                    if (preview is not null) result.Add(preview with { Avatar = _avatars.ReadConversation(row, preview.Key, viewport) });
                }
            }
            return true;
        }
        catch (ElementNotAvailableException) { _sessions = null; return false; }
    }

    private bool ReadMessages(List<WeChatMessage> result)
    {
        if (_messages is null) return false;
        try
        {
            var cache = new CacheRequest();
            cache.Add(AutomationElement.NameProperty); cache.Add(AutomationElement.ClassNameProperty);
            cache.Add(AutomationElement.ItemStatusProperty); cache.Add(AutomationElement.ItemTypeProperty);
            cache.Add(AutomationElement.BoundingRectangleProperty);
            using (cache.Activate())
            {
                var rows = _messages.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                for (var index = Math.Max(0, rows.Count - 80); index < rows.Count; index++)
                {
                    var row = rows[index];
                    var info = row.Cached;
                    if (info.ClassName == "mmui::ChatItemView") continue;
                    var body = info.Name.Trim();
                    if (body.Length == 0) continue;
                    if (body.Length > 4096) body = body[..4096];
                    var direction = Direction(info.ItemStatus, info.ItemType);
                    var sender = "";
                    if (!info.ClassName.StartsWith("mmui::", StringComparison.Ordinal))
                    {
                        foreach (AutomationElement button in row.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                        {
                            var avatar = button.Current;
                            var bounds = avatar.BoundingRectangle;
                            if (bounds.IsEmpty || info.BoundingRectangle.Width <= 0 || bounds.Width is < 20 or > 120
                                || Math.Abs(bounds.Width - bounds.Height) > 3 || avatar.Name.Length == 0) continue;
                            var position = (bounds.X - info.BoundingRectangle.X) / info.BoundingRectangle.Width;
                            if (position < 0.35) { direction = WeChatDirection.Incoming; sender = avatar.Name.Trim(); }
                            else if (position > 0.65) direction = WeChatDirection.Outgoing;
                            break;
                        }
                    }
                    result.Add(new(string.Join('/', row.GetRuntimeId()), sender, body, direction));
                }
            }
            return true;
        }
        catch (ElementNotAvailableException) { _messages = null; return false; }
    }

    private static string Name(AutomationElement? element)
    {
        try { return element?.Current.Name.Trim() ?? ""; }
        catch (ElementNotAvailableException) { return ""; }
    }

    private static WeChatDirection Direction(string status, string type)
    {
        var value = (status + " " + type).Trim();
        if (value.Equals("Incoming", StringComparison.OrdinalIgnoreCase) || value is "收到" or "接收") return WeChatDirection.Incoming;
        if (value.Equals("Outgoing", StringComparison.OrdinalIgnoreCase) || value is "发出" or "发送") return WeChatDirection.Outgoing;
        return WeChatDirection.Unknown;
    }

    public void Dispose() => _avatars.Dispose();
}
