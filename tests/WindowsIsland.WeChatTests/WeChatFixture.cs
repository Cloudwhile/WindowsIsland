using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

internal sealed class WeChatFixture : Window
{
    private readonly ListBox _sessions = new(), _messages = new();
    private readonly ListBoxItem _friend = new(), _group = new();
    private readonly TextBox _input = new();
    private int _unread = 7, _groupUnread = 4;

    private WeChatFixture(string pipe, string ready)
    {
        Title = "Windows Island WeChat fixture";
        Width = 740; Height = 520; ShowActivated = false;
        Left = 20; Top = 200;
        var content = new DockPanel();
        _sessions.Width = 260;
        DockPanel.SetDock(_sessions, Dock.Left);
        DockPanel.SetDock(_input, Dock.Bottom);
        _input.Height = 60;
        content.Children.Add(_sessions); content.Children.Add(_input); content.Children.Add(_messages);
        Content = content;
        AutomationProperties.SetAutomationId(_sessions, "session_list");
        AutomationProperties.SetName(_sessions, "会话");
        AutomationProperties.SetAutomationId(_messages, "chat_message_list");
        AutomationProperties.SetName(_messages, "消息");
        AutomationProperties.SetAutomationId(_input, "chat_input_field");
        AutomationProperties.SetName(_input, "示例会话");
        AutomationProperties.SetAutomationId(_friend, "session_item_示例会话");
        AutomationProperties.SetAutomationId(_group, "session_item_示例群");
        _sessions.Items.Add(_friend); _sessions.Items.Add(_group);
        Preview(_friend, "示例会话", "历史消息", 7);
        Preview(_group, "示例群", "群聊历史", 4);
        AddMessage("历史消息", "Incoming");
        Loaded += (_, _) =>
        {
            File.WriteAllText(ready, Environment.ProcessId.ToString());
            new Thread(() => Serve(pipe)) { IsBackground = true }.Start();
        };
    }

    public static int Run(string pipe, string ready)
    {
        var thread = new Thread(() => new Application().Run(new WeChatFixture(pipe, ready)));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        return 0;
    }

    private void Serve(string pipe)
    {
        try
        {
            using var server = new NamedPipeServerStream(pipe, PipeDirection.In);
            server.WaitForConnection();
            using var reader = new StreamReader(server);
            while (reader.ReadLine() is { } command)
                Dispatcher.Invoke(() => Apply(command));
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
            case "incoming":
                AddMessage(body, "Incoming"); Preview(_friend, "示例会话", body, ++_unread); break;
            case "outgoing":
                AddMessage(body, "Outgoing"); Preview(_friend, "示例会话", body, _unread = 0); break;
            case "group": Preview(_group, "示例群", body, ++_groupUnread); break;
            case "draft": Preview(_friend, "示例会话", "[草稿] " + body, _unread); break;
            case "history":
                _messages.Items.Clear(); AddMessage("更早的消息", "Incoming"); break;
            case "minimize": WindowState = WindowState.Minimized; break;
            case "mute": Preview(_group, "示例群", body, ++_groupUnread, true); break;
            case "unmute": Preview(_group, "示例群", body, _groupUnread); break;
            case "quit": Application.Current.Shutdown(); break;
        }
    }

    private void AddMessage(string body, string status)
    {
        var row = new ListBoxItem { Content = body };
        AutomationProperties.SetName(row, body);
        AutomationProperties.SetItemStatus(row, status);
        _messages.Items.Add(row);
    }

    private static void Preview(ListBoxItem row, string conversation, string body, int unread, bool muted = false)
    {
        var badge = unread > 0 ? $"[{unread}条]\n" : "";
        var name = $"{conversation}\n已置顶\n{badge}{body}\n{DateTime.Now:HH:mm}" + (muted ? "\n消息免打扰" : "");
        AutomationProperties.SetName(row, name);
        row.Content = new TextBlock { Text = name };
    }
}
