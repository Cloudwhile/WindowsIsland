using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using WindowsIsland.Components;
using WindowsIsland.Services;

namespace WindowsIsland;

public sealed class MainWindow : Window
{
    private const double CompactWidth = 120, CompactHeight = 32, SurfaceMargin = 12;
    private readonly IslandSurface _root;
    private readonly Grid _host = new();
    private readonly Grid _surfaceLayer;
    private readonly NotificationService _notifications;
    private readonly SettingsStore _settings;
    private SettingsWindow? _settingsWindow;
    private readonly MessengerHookService _messengers;
    private readonly TelegramNativeService _telegram;
    private readonly WeChatService _weChat;
    private readonly PowerService _power;
    private readonly MessageRouter _router = new();
    private readonly TrayIcon _tray;
    private readonly NotificationPresentation _notification = new();
    private readonly NotificationPanel _notificationPanel = new()
    {
        Visibility = Visibility.Collapsed
    };
    private readonly DispatcherQueueTimer _pulse, _expiry, _merge;
    private readonly IslandMotion _motion;
    private readonly NotificationInput _input;
    private readonly nint _hwnd;
    private bool _isExpanded, _closed, _polling, _started, _exiting, _openingSource;
    private double _width = CompactWidth, _height = CompactHeight;
    private double _notificationWidth = NotificationLayout.MinWidth, _notificationHeight = NotificationLayout.MinHeight;
    private double _scale = 1;
    private RectInt32 _workArea;
    private RectInt32? _lastBounds;
    private int _ticks;
    private IslandNotification? _current;
    private readonly bool _verification;

    internal MainWindow(SettingsStore settings, bool verification = false)
    {
        _settings = settings;
        _verification = verification;
        Title = "Windows Island";
        _root = new IslandSurface(_notificationPanel)
        {
            Width = _width, Height = _height
        };
        _surfaceLayer = new Grid
        {
            Width = _width, Height = _height, Margin = new Thickness(SurfaceMargin),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top
        };
        _surfaceLayer.Children.Add(_root);
        _host.Children.Add(_surfaceLayer);
        Content = _host;
        SystemBackdrop = new OverlayBackdrop();
        AutomationProperties.SetName(_root, "灵动岛");
        AutomationProperties.SetAutomationId(_root, "IslandSurface");
        AutomationProperties.SetAutomationId(_notificationPanel, "NotificationContent");
        _notifications = new NotificationService(DispatcherQueue);
        _notifications.SetEnabled(_settings.Current.SystemNotifications);
        _notifications.Received += ReceiveNotification;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _input = new NotificationInput(_hwnd, right => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed || !_notification.Active) return;
            if (right) DismissNotification();
            else _ = OpenNotificationSourceAsync();
        }));
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        NativeWindow.RemoveSystemBorder(_hwnd);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Hide();
        NativeWindow.ConfigureOverlay(_hwnd);
        _messengers = new MessengerHookService(DispatcherQueue, verification
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "bin", "WindowsIsland.HookFixture")) : null,
            identity => _settings.Current.AllowsClient(identity));
        _messengers.Received += ReceiveNotification;
        _telegram = new TelegramNativeService(DispatcherQueue, verification);
        _messengers.NativeTelegramConnected = _telegram.IsConnected;
        _telegram.Received += ReceiveNotification;
        _weChat = new WeChatService(() => _settings.Current.WeChat,
            action => DispatcherQueue.TryEnqueue(() => { if (!_closed) action(); }), verification
                ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "bin", "WindowsIsland.WeChatTests")) : null);
        _weChat.Received += ReceiveNotification;
        _power = new PowerService(_hwnd, DispatcherQueue);
        _power.Received += ReceiveNotification;
        _tray = new TrayIcon(_hwnd,
            () => DispatcherQueue.TryEnqueue(ExitApplication),
            () => DispatcherQueue.TryEnqueue(OpenSettings),
            () => DispatcherQueue.TryEnqueue(OpenSettings));
        _settings.Changed += OnSettingsChanged;
        _notifications.AccessChanged += access =>
        {
            if (_closed) return;
            _tray.UpdateAccess(access, _settings.Current.SystemNotifications);
            if (access != NotificationAccess.Allowed)
            {
                _router.ClearSystemHistory();
                if (_current?.Source == NotificationSource.SystemNotification) DismissNotification(animate: false);
            }
        };

        _motion = new IslandMotion(_surfaceLayer, _notificationPanel, verification);
        _motion.Settled += () => UpdateInputRegion(_width, _height);
        _merge = DispatcherQueue.CreateTimer();
        _merge.Interval = TimeSpan.FromMilliseconds(125);
        _merge.Tick += (_, _) =>
        {
            foreach (var dispatch in _router.Flush()) DispatchMessage(dispatch);
            if (!_router.HasPending) _merge.Stop();
        };
        _expiry = DispatcherQueue.CreateTimer();
        _expiry.Interval = TimeSpan.FromMilliseconds(250);
        _expiry.Tick += (_, _) =>
        {
            if (!_closed && _notification.Expired) DismissNotification();
            else if (!_closed && _notification.Active && AppWindow.IsVisible) NativeWindow.EnsureTopmost(_hwnd);
        };
        _pulse = DispatcherQueue.CreateTimer();
        _pulse.Interval = TimeSpan.FromSeconds(1);
        _pulse.Tick += Pulse;
        AppWindow.Closing += (_, args) =>
        {
            if (_exiting || _closed) return;
            args.Cancel = true;
            DismissNotification(animate: false);
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _settingsWindow?.Close();
            _settings.Changed -= OnSettingsChanged;
            _pulse.Stop();
            _motion.Dispose();
            _input.Dispose();
            _expiry.Stop();
            _merge.Stop();
            _router.Clear();
            _messengers.Dispose();
            _telegram.Dispose();
            _weChat.Dispose();
            _power.Dispose();
            _notifications.Dispose();
            _tray.Dispose();
        };
        RefreshDisplayMetrics();
        ApplyBounds();
    }

    public async Task StartListeningAsync()
    {
        if (_started || _closed) return;
        _started = true;
        _messengers.Start();
        _weChat.Start();
        _telegram.RefreshClients(TelegramClients());
        _pulse.Start();
        await _notifications.InitializeAsync(requestAccess: _verification);
    }

    internal void OpenSettings()
    {
        if (_closed) return;
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settings,
                () => new SettingsSnapshot(_notifications.Access, AppInitialization.HasIdentity, _messengers.ConnectedApps, _weChat.Status),
                InitializeApplicationAsync, RequestNotificationAccessAsync, PreviewNotification);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Activate();
    }

    private async Task InitializeApplicationAsync()
    {
        var appId = await AppInitialization.InitializeAsync();
        if (!AppInitialization.HasIdentity) ((App)Application.Current).RestartRegistered(appId);
    }

    private void PreviewNotification()
    {
        if (_closed) return;
        byte[]? icon = null;
        try { icon = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "Island.ico")); }
        catch (IOException) { }
        ShowNotification(new IslandNotification(0, DateTimeOffset.Now, "Windows Island", "通知预览", "你好，通知已准备就绪。", icon));
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        if (_closed) return;
        _router.Clear();
        _merge.Stop();
        if (_current is not null && !settings.Allows(_current)) DismissNotification(animate: false);
        _notifications.SetEnabled(settings.SystemNotifications);
        _tray.UpdateAccess(_notifications.Access, settings.SystemNotifications);
        if (!_started) return;
        _messengers.RefreshClients();
        _weChat.Refresh();
        _telegram.RefreshClients(TelegramClients());
        if (settings.SystemNotifications) _ = _notifications.InitializeAsync(requestAccess: false);
    }

    private int[] TelegramClients()
    {
        if (!_verification) return _messengers.TelegramProcessIds;
        var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "native", "Release", "WindowsIsland.TelegramFixture.exe"));
        var clients = new List<int>();
        foreach (var process in Process.GetProcessesByName("WindowsIsland.TelegramFixture"))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) clients.Add(process.Id);
                }
                catch (Exception) { }
            }
        }
        return clients.ToArray();
    }

    internal void ExitApplication()
    {
        if (_closed) return;
        _exiting = true;
        _settingsWindow?.Close();
        Close();
    }

    private async Task RequestNotificationAccessAsync()
    {
        if (_closed) return;
        if (_notifications.Access == NotificationAccess.Allowed)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-notifications"));
            return;
        }
        await _notifications.InitializeAsync();
        if (_closed || _notifications.Access != NotificationAccess.Denied) return;
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-notifications")); }
        catch (Exception) { _tray.UpdateAccess(NotificationAccess.Unavailable); }
    }

    private void UpdatePresentation(bool newMessage)
    {
        _input.Hide();
        var wasVisible = AppWindow.IsVisible;
        var previous = new Size(_width, _height);
        var target = new Size(_notificationWidth, _notificationHeight);
        _width = _root.Width = _surfaceLayer.Width = target.Width;
        _height = _root.Height = _surfaceLayer.Height = target.Height;
        _isExpanded = true;
        _notificationPanel.Visibility = Visibility.Visible;
        ApplyBounds();
        if (!wasVisible)
        {
            AppWindow.Show(false);
            NativeWindow.RemoveSystemBorder(_hwnd);
        }
        NativeWindow.ConfigureOverlay(_hwnd);
        _host.UpdateLayout();
        _motion.Show(previous, target, wasVisible, newMessage);
    }

    private void UpdateInputRegion(double width, double height)
    {
        if (_closed || !AppWindow.IsVisible || _lastBounds is not { } bounds) return;
        var contentWidth = (int)Math.Ceiling(width * _scale);
        _input.Show(new RectInt32(bounds.X + (bounds.Width - contentWidth) / 2,
            bounds.Y + (int)Math.Round(SurfaceMargin * _scale), contentWidth, (int)Math.Ceiling(height * _scale)),
            (int)Math.Ceiling(NotificationLayout.CornerRadius * _scale));
    }

    private async Task OpenNotificationSourceAsync()
    {
        if (_closed || _openingSource || _current is not { } notification) return;
        _openingSource = true;
        try
        {
            if (await NotificationActivation.OpenAsync(notification) && ReferenceEquals(_current, notification)) DismissNotification();
        }
        finally { _openingSource = false; }
    }

    private void ApplyBounds()
    {
        var width = Math.Max(1, (int)Math.Round(Math.Min(NotificationLayout.MaxWidth + SurfaceMargin * 2, _workArea.Width / _scale) * _scale));
        var height = Math.Max(1, (int)Math.Round(Math.Min(NotificationLayout.MaxHeight + SurfaceMargin * 2, _workArea.Height / _scale) * _scale));
        var x = _workArea.X + (_workArea.Width - width) / 2;
        var y = _workArea.Y;
        var bounds = new RectInt32(x, y, width, height);
        if (_lastBounds != bounds)
        {
            AppWindow.MoveAndResize(bounds);
            _lastBounds = bounds;
        }
        NativeWindow.SetContentBounds(_hwnd, (int)((width - _width * _scale) / 2), (int)(SurfaceMargin * _scale),
            (int)Math.Ceiling(_width * _scale), (int)Math.Ceiling(_height * _scale), (int)(NotificationLayout.CornerRadius * _scale));
        if (AppWindow.IsVisible) NativeWindow.EnsureTopmost(_hwnd);
        if (_isExpanded && !_motion.IsRunning) UpdateInputRegion(_width, _height);
    }

    private void RefreshDisplayMetrics()
    {
        _scale = Math.Max(1, NativeWindow.GetDpiForWindow(_hwnd) / 96d);
        _workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
    }

    private async void Pulse(DispatcherQueueTimer sender, object args)
    {
        if (_closed) return;
        _tray.EnsureAdded();
        _power.Poll();
        if (++_ticks % 5 == 0)
        {
            _messengers.RefreshClients();
            _telegram.RefreshClients(TelegramClients());
        }
        if (_polling) return;
        _polling = true;
        try
        {
            await _notifications.PollAsync();
            if (!_closed && _isExpanded && !_motion.IsRunning)
            {
                RefreshDisplayMetrics();
                ApplyBounds();
            }
        }
        finally { _polling = false; }
    }

    private bool AllowsNotification(IslandNotification notification)
    {
        if (_closed || !_settings.Current.Allows(notification)) return false;
        if (MessengerIdentity.FromNotification(notification) == "wechat"
            && !_weChat.AllowsConversation(notification.Title, notification.OriginProcessId)) return false;
        if (_verification && notification.Source == NotificationSource.SystemNotification
            && notification.AppId != Windows.ApplicationModel.Package.Current.Id.FamilyName + "!App") return false;
        return true;
    }

    private void ReceiveNotification(IslandNotification notification)
    {
        if (!AllowsNotification(notification)) return;
        var dispatch = _router.Receive(notification);
        if (dispatch is not null) DispatchMessage(dispatch);
        if (_router.HasPending) _merge.Start();
        else _merge.Stop();
    }

    private void DispatchMessage(MessageDispatch dispatch)
    {
        if (!AllowsNotification(dispatch.Notification)) return;
        if (dispatch.ReplaceCurrent)
        {
            if (!_notification.Active || _current is null
                || !MessageRouter.Matches(_current, dispatch.Notification)) return;
            _current = dispatch.Notification;
            _notificationPanel.Show(dispatch.Notification);
            MeasureNotification();
            UpdatePresentation(newMessage: false);
            if (AppWindow.IsVisible) NativeWindow.EnsureTopmost(_hwnd);
            return;
        }
        ShowNotification(dispatch.Notification);
    }

    private void ShowNotification(IslandNotification notification)
    {
        if (_closed) return;
        _current = notification;
        _notification.Show();
        _expiry.Start();
        _notificationPanel.Show(notification);
        MeasureNotification();
        UpdatePresentation(newMessage: true);
    }

    private void MeasureNotification()
    {
        RefreshDisplayMetrics();
        var size = _notificationPanel.MeasureWindow(_workArea.Width / _scale - 24, _workArea.Height / _scale - 24);
        _notificationWidth = size.Width;
        _notificationHeight = size.Height;
    }

    private void DismissNotification(bool animate = true)
    {
        if (_closed) return;
        _expiry.Stop();
        _current = null;
        _input.Hide();
        _notification.Clear();
        _isExpanded = false;
        if (animate && AppWindow.IsVisible) _motion.Hide(HideIsland);
        else HideIsland();
    }

    private void HideIsland()
    {
        _motion.Reset();
        _isExpanded = false;
        _width = CompactWidth;
        _height = CompactHeight;
        _notificationPanel.Clear();
        _notificationPanel.Visibility = Visibility.Collapsed;
        _input.Hide();
        AppWindow.Hide();
    }
}
