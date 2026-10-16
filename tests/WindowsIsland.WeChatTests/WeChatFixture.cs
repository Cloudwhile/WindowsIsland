using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal sealed class WeChatFixture : Window
{
    private readonly ListBox _sessions = new SessionList(), _messages = new();
    private readonly ListBoxItem _friend = new(), _group = new SessionRow(), _service = new SessionRow("mmui::BrandSessionCell");
    private ListBoxItem _newChat = new SessionRow();
    private readonly TextBox _input = new();
    private readonly Portrait _groupPortrait;
    private readonly Portrait _unavailablePortrait;
    private string _newChatBody = "";
    private int _unread = 7, _groupUnread = 4;
    private readonly string? _accessibilityModule;
    private readonly DockPanel _content;
    private nint _module, _window;

    private WeChatFixture(string pipe, string ready, string? accessibilityModule)
    {
        _accessibilityModule = accessibilityModule;
        Title = "Windows Island WeChat fixture";
        Width = 740; Height = 520; ShowActivated = false;
        Left = 20; Top = 200;
        var content = _content = new DockPanel();
        _sessions.Width = 260;
        DockPanel.SetDock(_sessions, Dock.Left);
        DockPanel.SetDock(_input, Dock.Bottom);
        _input.Height = 60;
        content.Children.Add(_sessions); content.Children.Add(_input); content.Children.Add(_messages);
        Content = accessibilityModule is null ? content : null;
        var accessibilityTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        accessibilityTimer.Tick += (_, _) =>
        {
            if (_accessibilityModule is not null) Content = _module != 0 && Marshal.ReadByte(_module + 0x2021) == 1 ? _content : null;
        };
        if (accessibilityModule is not null) accessibilityTimer.Start();
        AutomationProperties.SetAutomationId(_sessions, "session_list");
        AutomationProperties.SetName(_sessions, "会话");
        AutomationProperties.SetAutomationId(_messages, "chat_message_list");
        AutomationProperties.SetName(_messages, "消息");
        AutomationProperties.SetAutomationId(_input, "chat_input_field");
        AutomationProperties.SetName(_input, "示例会话");
        AutomationProperties.SetAutomationId(_friend, "session_item_示例会话");
        AutomationProperties.SetAutomationId(_group, "session_item_示例群");
        AutomationProperties.SetAutomationId(_service, "session_item_服务号");
        _friend.Tag = CreateAvatar(false); _group.Tag = _groupPortrait = CreateAvatar(true);
        _service.Tag = CreateAvatar(true, true);
        _newChat.Tag = _unavailablePortrait = CreateAvatar(true, blank: true);
        AutomationProperties.SetAutomationId(_newChat, "session_item_首次出现的会话");
        Closed += (_, _) =>
        {
            accessibilityTimer.Stop();
            if (_module != 0) FreeLibrary(_module);
            foreach (var row in new[] { _friend, _group, _service })
                if (row.Tag is Portrait portrait) File.Delete(portrait.Path);
            File.Delete(_groupPortrait.Path);
            File.Delete(_unavailablePortrait.Path);
        };
        _sessions.Items.Add(_friend); _sessions.Items.Add(_group); _sessions.Items.Add(_service);
        Preview(_friend, "示例会话", "历史消息", 7);
        Preview(_group, "示例群", "群聊历史", 4);
        Preview(_service, "服务号", "服务通知", 0);
        AddMessage("历史消息", "Incoming");
        Loaded += (_, _) =>
        {
            _window = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            File.WriteAllText(ready, Environment.ProcessId.ToString());
            new Thread(() => Serve(pipe)) { IsBackground = true }.Start();
        };
    }

    public static int Run(string pipe, string ready, string? accessibilityModule = null)
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var thread = new Thread(() => new Application().Run(new WeChatFixture(pipe, ready, accessibilityModule)));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        return 0;
    }

    private void Serve(string pipe)
    {
        try
        {
            using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut);
            server.WaitForConnection();
            using var reader = new StreamReader(server, leaveOpen: true);
            using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
            writer.WriteLine(_window.ToInt64());
            while (reader.ReadLine() is { } command)
            {
                Dispatcher.Invoke(() => Apply(command));
                writer.WriteLine("ok");
            }
        }
        catch (Exception error) when (error is IOException or TaskCanceledException or InvalidOperationException) { }
    }

    private void Apply(string command)
    {
        var separator = command.IndexOf(':');
        var action = separator < 0 ? command : command[..separator];
        var body = separator < 0 ? "" : command[(separator + 1)..];
        switch (action)
        {
            case "accessibility-load": _module = LoadLibraryEx(_accessibilityModule!, 0, 1); break;
            case "accessibility-reset":
                if (_module != 0) Marshal.WriteByte(_module + 0x2021, 0);
                Content = null;
                break;
            case "incoming":
                AddMessage(body, "Incoming"); Preview(_friend, "示例会话", body, ++_unread); break;
            case "outgoing":
                AddMessage(body, "Outgoing"); Preview(_friend, "示例会话", body, _unread = 0); break;
            case "remote-outgoing":
                AddMessage(body, ""); Preview(_friend, "示例会话", body, _unread); break;
            case "group": Preview(_group, "示例群", body, ++_groupUnread); break;
            case "newchat":
                _newChatBody = body;
                Preview(_newChat, "首次出现的会话", body, 1, pinned: false);
                if (!_sessions.Items.Contains(_newChat)) _sessions.Items.Add(_newChat);
                break;
            case "newchat-avatar":
                var newChatIndex = _sessions.Items.IndexOf(_newChat);
                if (newChatIndex >= 0) _sessions.Items.RemoveAt(newChatIndex);
                _newChat = new ListBoxItem { Tag = _friend.Tag };
                AutomationProperties.SetAutomationId(_newChat, "session_item_首次出现的会话");
                Preview(_newChat, "首次出现的会话", _newChatBody, 1, pinned: false);
                if (newChatIndex >= 0) _sessions.Items.Insert(newChatIndex, _newChat);
                break;
            case "draft": Preview(_friend, "示例会话", "[草稿] " + body, _unread); break;
            case "history":
                _messages.Items.Clear(); AddMessage("更早的消息", "Incoming"); break;
            case "minimize": WindowState = WindowState.Minimized; break;
            case "restore": WindowState = WindowState.Normal; break;
            case "mute": Preview(_group, "示例群", body, ++_groupUnread, true); break;
            case "unmute": Preview(_group, "示例群", body, _groupUnread); break;
            case "avatar-recycle":
                _group.Tag = _service.Tag;
                AutomationProperties.SetAutomationId(_group, "session_item_替换后的会话");
                Preview(_group, "替换后的会话", "缓存检查", 0); break;
            case "avatar-reorder":
                _sessions.Items.Remove(_group); _sessions.Items.Insert(0, _group); break;
            case "avatar-reset":
                _sessions.Items.Remove(_group); _sessions.Items.Insert(1, _group);
                _group.Tag = _groupPortrait;
                AutomationProperties.SetAutomationId(_group, "session_item_示例群");
                Preview(_group, "示例群", "头像检查完成", 0); break;
            case "quit": Application.Current.Shutdown(); break;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode)] private static extern nint LoadLibraryEx(string path, nint file, uint flags);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(nint module);

    private void AddMessage(string body, string status)
    {
        var row = new ListBoxItem { Content = body };
        AutomationProperties.SetName(row, body);
        AutomationProperties.SetItemStatus(row, status);
        _messages.Items.Add(row);
    }

    private static void Preview(ListBoxItem row, string conversation, string body, int unread, bool muted = false, bool pinned = true)
    {
        var badge = unread > 0 ? $"[{unread}条]\n" : "";
        var name = conversation + (pinned ? "\n已置顶" : "") + $"\n{badge}{body}\n{DateTime.Now:HH:mm}" + (muted ? "\n消息免打扰" : "");
        AutomationProperties.SetName(row, name);
        var portrait = (Portrait)row.Tag;
        row.Height = 68; row.Padding = new Thickness(0); row.BorderThickness = new Thickness(0);
        row.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var content = new Grid();
        var avatar = new Image { Source = portrait.Image, Width = 40, Height = 40, Margin = new Thickness(12, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        if (portrait.ExposeReference) AutomationProperties.SetHelpText(avatar, new Uri(portrait.Path).AbsoluteUri);
        content.Children.Add(avatar);
        content.Children.Add(new TextBlock { Text = name, Margin = new Thickness(64, 8, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        row.Content = content;
    }

    private sealed record Portrait(string Path, ImageSource Image, bool ExposeReference);

    private static Portrait CreateAvatar(bool group, bool service = false, bool blank = false)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N") + ".avatar.png");
        using (var bitmap = new System.Drawing.Bitmap(64, 64))
        {
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.Clear(service ? System.Drawing.Color.DarkOrange : group ? System.Drawing.Color.ForestGreen : System.Drawing.Color.CornflowerBlue);
            using var first = new System.Drawing.SolidBrush(System.Drawing.Color.Gold);
            using var second = new System.Drawing.SolidBrush(System.Drawing.Color.HotPink);
            if (service) graphics.FillRectangle(System.Drawing.Brushes.White, 16, 16, 32, 32);
            else if (!blank)
            {
                graphics.FillRectangle(first, 18, 18, 14, 28);
                graphics.FillRectangle(second, group ? 34 : 24, group ? 24 : 34, 14, 14);
            }
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path); image.EndInit(); image.Freeze();
        return new(path, image, !group);
    }

    private sealed class SessionRow(string className = "mmui::ChatSessionCell") : ListBoxItem
    {
        public string AccessibleClass { get; } = className;
    }

    private sealed class SessionList : ListBox
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new SessionListPeer(this);
        private sealed class SessionListPeer(SessionList owner) : ListBoxAutomationPeer(owner)
        {
            protected override ItemAutomationPeer CreateItemAutomationPeer(object item) => item is SessionRow
                ? new SessionPeer(item, this) : base.CreateItemAutomationPeer(item);
        }
        private sealed class SessionPeer(object item, SelectorAutomationPeer parent) : ListBoxItemAutomationPeer(item, parent)
        {
            protected override string GetClassNameCore() => ((SessionRow)Item).AccessibleClass;
            protected override List<AutomationPeer> GetChildrenCore() => [];
        }
    }
}
