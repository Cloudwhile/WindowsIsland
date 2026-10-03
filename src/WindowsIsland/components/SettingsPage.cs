using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed record SettingsSnapshot(NotificationAccess Access, bool Initialized, IReadOnlyCollection<string> ConnectedApps, string WeChatStatus);

internal sealed class SettingsPage : Grid, IDisposable
{
    private readonly SettingsStore _store;
    private readonly Func<SettingsSnapshot> _snapshot;
    private readonly Action _close;
    private readonly InfoBar _notice = new() { IsOpen = true, IsClosable = false };
    private readonly ProgressRing _progress = new() { Width = 20, Height = 20, Visibility = Visibility.Collapsed };
    private readonly IconActionButton _initialize = new(Symbol.Download, "初始化应用", "InitializeApplication");
    private readonly IconActionButton _permission = new(Symbol.Permissions, "允许通知访问", "RequestNotificationAccess");
    private readonly IconActionButton _complete = new(Symbol.Accept, "完成初始化", "CompleteSetup");
    private readonly IconActionButton _installSettings = new(Symbol.Setting, "打开系统设置", "OpenInstallationSettings");
    private readonly SettingsRow _initializationRow, _permissionRow, _systemRow, _weChatRow, _telegramRow, _powerRow;
    private readonly ToggleSwitch _system, _weChat, _telegram, _power, _animations;
    private readonly List<Control> _actions = [];
    private bool _busy, _syncing, _failed, _disposed;

    public SettingsPage(SettingsStore store, Func<SettingsSnapshot> snapshot, Func<Task> initialize,
        Func<Task> requestAccess, Action preview, Action close)
    {
        _store = store;
        _snapshot = snapshot;
        _close = close;
        var content = new StackPanel { Spacing = 8, Margin = new Thickness(24), MaxWidth = 720 };
        var heading = new Grid { ColumnSpacing = 8 };
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = IslandTheme.Text("设置", 28);
        title.FontWeight = FontWeights.SemiBold;
        heading.Children.Add(title);
        var refresh = new IconActionButton(Symbol.Refresh, "刷新状态", "RefreshSettings");
        refresh.Click += (_, _) => { _failed = false; Refresh(); };
        Grid.SetColumn(_progress, 1);
        Grid.SetColumn(refresh, 2);
        Grid.SetColumn(_complete, 3);
        heading.Children.Add(_progress);
        heading.Children.Add(refresh);
        heading.Children.Add(_complete);
        content.Children.Add(heading);
        content.Children.Add(_notice);
        content.Children.Add(Section("初始化"));
        _initializationRow = new SettingsRow(Symbol.Download, "应用初始化", _initialize);
        _permissionRow = new SettingsRow(Symbol.Permissions, "通知访问", _permission);
        var previewButton = new IconActionButton(Symbol.Play, "显示测试通知", "PreviewNotification");
        previewButton.Click += (_, _) => preview();
        var previewRow = new SettingsRow(Symbol.View, "通知预览", previewButton) { Status = "可测试" };
        content.Children.Add(_initializationRow);
        content.Children.Add(_permissionRow);
        content.Children.Add(previewRow);
        content.Children.Add(Section("外观"));
        _animations = Switch("弹窗动画", "AnimationsToggle", settings => settings.Animations,
            (settings, value) => settings with { Animations = value });
        content.Children.Add(new SettingsRow(Symbol.Play, "弹窗动画", _animations));
        content.Children.Add(Section("消息来源"));
        _system = Switch("系统通知", "SystemNotificationsToggle", settings => settings.SystemNotifications,
            (settings, value) => settings with { SystemNotifications = value });
        _weChat = Switch("微信", "WeChatToggle", settings => settings.WeChat, (settings, value) => settings with { WeChat = value });
        _telegram = Switch("Telegram", "TelegramToggle", settings => settings.Telegram, (settings, value) => settings with { Telegram = value });
        _power = Switch("电源", "PowerToggle", settings => settings.Power, (settings, value) => settings with { Power = value });
        _systemRow = new SettingsRow(Symbol.Message, "系统通知", _system);
        _weChatRow = new SettingsRow(Symbol.Contact, "微信", _weChat);
        _telegramRow = new SettingsRow(Symbol.Send, "Telegram", _telegram);
        _powerRow = new SettingsRow(new FontIcon { Glyph = char.ConvertFromUtf32(0xE7E8), FontSize = 20 }, "电源", _power);
        foreach (var row in new[] { _systemRow, _weChatRow, _telegramRow, _powerRow }) content.Children.Add(row);
        Children.Add(new ScrollViewer
        {
            Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        _actions.AddRange([_initialize, _permission, refresh, _complete, previewButton, _installSettings]);
        _initialize.Click += async (_, _) => await RunAsync(initialize, "初始化未完成，请打开系统设置后重试。", showInstallSettings: true);
        _permission.Click += async (_, _) => await RunAsync(requestAccess, "通知访问暂时不可用，请重试。");
        _installSettings.Click += async (_, _) => await RunAsync(async () =>
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:developers")))
                throw new InvalidOperationException("Windows settings did not open.");
        }, "系统设置暂时无法打开，请重试。");
        _complete.Click += (_, _) => Complete();
        _store.Changed += OnSettingsChanged;
        AutomationProperties.SetAutomationId(this, "SettingsPage");
        AutomationProperties.SetAutomationId(_notice, "SetupStatus");
        Refresh();
    }

    private static TextBlock Section(string title)
    {
        var text = IslandTheme.Text(title, 20);
        text.FontWeight = FontWeights.SemiBold;
        text.Margin = new Thickness(0, 16, 0, 0);
        return text;
    }

    private ToggleSwitch Switch(string label, string id, Func<AppSettings, bool> read,
        Func<AppSettings, bool, AppSettings> update)
    {
        var toggle = new ToggleSwitch { IsOn = read(_store.Current), OnContent = "开", OffContent = "关", VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, label);
        AutomationProperties.SetAutomationId(toggle, id);
        toggle.Toggled += (_, _) =>
        {
            if (_syncing || _disposed) return;
            try { _store.Save(update(_store.Current, toggle.IsOn)); _failed = false; Refresh(); }
            catch (Exception error) { Trace.WriteLine(error); ShowError("设置未能保存，请重试。"); Refresh(); }
        };
        _actions.Add(toggle);
        return toggle;
    }

    private async Task RunAsync(Func<Task> action, string message, bool showInstallSettings = false)
    {
        if (_busy || _disposed) return;
        _busy = true;
        _failed = false;
        _notice.ActionButton = null;
        _notice.Title = "正在处理";
        _notice.Message = "请稍候。";
        _notice.Severity = InfoBarSeverity.Informational;
        _progress.IsActive = true;
        _progress.Visibility = Visibility.Visible;
        Refresh();
        try { await action(); }
        catch (Exception error)
        {
            Trace.WriteLine(error);
            if (!_disposed) ShowError(message, showInstallSettings);
        }
        finally
        {
            if (!_disposed)
            {
                _busy = false;
                _progress.IsActive = false;
                _progress.Visibility = Visibility.Collapsed;
                Refresh();
            }
        }
    }

    private void Complete()
    {
        var state = _snapshot();
        if (_store.Current.SystemNotifications && (!state.Initialized || state.Access != NotificationAccess.Allowed))
        {
            Refresh();
            return;
        }
        try { _store.Save(_store.Current with { SetupCompleted = true }); _close(); }
        catch (Exception error) { Trace.WriteLine(error); ShowError("初始化状态未能保存，请重试。"); }
    }

    private void ShowError(string message, bool showInstallSettings = false)
    {
        _failed = true;
        _notice.Title = "未完成";
        _notice.Message = message;
        _notice.Severity = InfoBarSeverity.Error;
        _notice.ActionButton = showInstallSettings ? _installSettings : null;
    }

    private void OnSettingsChanged(AppSettings _) => Refresh();

    public void Refresh()
    {
        if (_disposed) return;
        var state = _snapshot();
        var settings = _store.Current;
        _syncing = true;
        try
        {
            _system.IsOn = settings.SystemNotifications;
            _weChat.IsOn = settings.WeChat;
            _telegram.IsOn = settings.Telegram;
            _power.IsOn = settings.Power;
            _animations.IsOn = settings.Animations;
        }
        finally { _syncing = false; }
        foreach (var action in _actions) action.IsEnabled = !_busy;
        _initializationRow.Status = state.Initialized ? "已完成" : settings.SystemNotifications ? "待完成" : "可跳过";
        _permissionRow.Status = state.Access switch
        {
            NotificationAccess.Allowed => "已允许",
            NotificationAccess.Denied => "未允许",
            NotificationAccess.NeedsRegistration => "请先完成应用初始化",
            NotificationAccess.Unavailable => "连接暂时不可用",
            _ => "等待授权"
        };
        _initialize.IsEnabled = !_busy && !state.Initialized && settings.SystemNotifications;
        _permission.IsEnabled = !_busy && state.Initialized && settings.SystemNotifications;
        _permission.SetAction(state.Access == NotificationAccess.Allowed ? Symbol.Setting : Symbol.Permissions,
            state.Access == NotificationAccess.Allowed ? "管理通知访问" : "允许通知访问");
        var ready = !settings.SystemNotifications || state.Initialized && state.Access == NotificationAccess.Allowed;
        _complete.IsEnabled = !_busy && ready;
        _complete.SetAction(Symbol.Accept, settings.SetupCompleted ? "关闭设置" : "完成初始化");
        _systemRow.Status = !settings.SystemNotifications ? "已关闭" : state.Access == NotificationAccess.Allowed ? "已开启" : "等待授权";
        _weChatRow.Status = settings.WeChat ? state.WeChatStatus : "已关闭";
        _telegramRow.Status = ClientStatus(settings.Telegram, state.ConnectedApps.Contains("telegram"));
        _powerRow.Status = settings.Power ? "已开启" : "已关闭";
        if (_busy || _failed) return;
        _notice.ActionButton = null;
        _notice.Title = ready ? "已就绪" : "初始化未完成";
        _notice.Message = ready ? "设置已保存。" : state.Initialized ? "请允许通知访问，或关闭系统通知后继续。" : "请先完成应用初始化，再允许通知访问。";
        _notice.Severity = ready ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
    }

    private static string ClientStatus(bool enabled, bool connected) => !enabled ? "已关闭" : connected ? "已连接" : "未运行";

    public void Dispose()
    {
        _disposed = true;
        _store.Changed -= OnSettingsChanged;
    }
}
