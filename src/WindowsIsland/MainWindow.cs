using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using WindowsIsland.Components;
using WindowsIsland.Services;

namespace WindowsIsland;

public sealed class MainWindow : Window
{
    private const double CompactWidth = 120, CompactHeight = 32;
    private readonly IslandSurface _root;
    private readonly NotificationService _notifications;
    private readonly SettingsStore _settings;
    private SettingsWindow? _settingsWindow;
    private readonly MessengerHookService _messengers;
    private readonly TelegramNativeService _telegram;
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
    private readonly nint _hwnd;
    private bool _isExpanded, _closed, _polling, _started, _exiting;
    private double _width = CompactWidth, _height = CompactHeight;
    private double _notificationWidth = NotificationLayout.MinWidth, _notificationHeight = NotificationLayout.MinHeight;
    private double _fromWidth, _fromHeight, _toWidth, _toHeight;
    private double _scale = 1;
    private RectInt32 _workArea;
    private RectInt32? _lastBounds;
    private int _lastRadius = -1, _ticks;
    private IslandNotification? _current;
    private readonly bool _verification;

    internal MainWindow(SettingsStore settings, bool verification = false)
    {
        _settings = settings;
        _verification = verification;
        Title = "Windows Island";
        _root = new IslandSurface(_notificationPanel);
        Content = _root;
        SystemBackdrop = new DesktopAcrylicBackdrop();
        AutomationProperties.SetName(_root, "灵动岛");
        AutomationProperties.SetAutomationId(_root, "IslandSurface");
        AutomationProperties.SetAutomationId(_notificationPanel, "NotificationContent");
        _notifications = new NotificationService(DispatcherQueue);
        _notifications.SetEnabled(_settings.Current.SystemNotifications);
        _notifications.Received += ReceiveNotification;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
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

        _motion = new IslandMotion(Animate);
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
            _motion.Stop();
            _expiry.Stop();
            _merge.Stop();
            _router.Clear();
            _messengers.Dispose();
            _telegram.Dispose();
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
                () => new SettingsSnapshot(_notifications.Access, AppInitialization.HasIdentity, _messengers.ConnectedApps),
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

    private void SetExpanded(bool expanded)
    {
        if (_closed) return;
        var width = expanded ? _notificationWidth : CompactWidth;
        var height = expanded ? _notificationHeight : CompactHeight;
        if (_isExpanded == expanded && Math.Abs(_toWidth - width) < 0.5 && Math.Abs(_toHeight - height) < 0.5) return;
        _isExpanded = expanded;
        _notificationPanel.Visibility = Visibility.Collapsed;
        _fromWidth = _width; _fromHeight = _height;
        _toWidth = width;
        _toHeight = height;
        _motion.Start();
    }

    private void Animate(double t)
    {
        var ease = 1 - Math.Pow(1 - t, 4);
        _width = _fromWidth + (_toWidth - _fromWidth) * ease;
        _height = _fromHeight + (_toHeight - _fromHeight) * ease;
        ApplyBounds();
        if (t < 1) return;
        _motion.Stop();
        if (!_isExpanded) { HideIsland(); return; }
        _notificationPanel.Visibility = Visibility.Visible;
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(140)) };
        Storyboard.SetTarget(fade, _notificationPanel);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }

    private void ApplyBounds()
    {
        var scale = _scale;
        var area = _workArea;
        var width = Math.Max(1, (int)Math.Round(_width * scale));
        var height = Math.Max(1, (int)Math.Round(_height * scale));
        var x = area.X + (area.Width - width) / 2;
        var y = area.Y + (int)(12 * scale);
        var radius = (int)(Math.Min(NotificationLayout.CornerRadius, _height / 2) * scale);
        var sameSize = _lastBounds is { } oldSize && oldSize.Width == width && oldSize.Height == height;
        if (_lastBounds is { } old && old.X == x && old.Y == y && sameSize && _lastRadius == radius) return;
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        if (!sameSize || _lastRadius != radius) NativeWindow.Round(_hwnd, width, height, radius);
        if (AppWindow.IsVisible) NativeWindow.EnsureTopmost(_hwnd);
        _lastBounds = new RectInt32(x, y, width, height);
        _lastRadius = radius;
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

    private void ReceiveNotification(IslandNotification notification)
    {
        if (_closed || !_settings.Current.Allows(notification)) return;
        if (_verification && notification.Source == NotificationSource.SystemNotification
            && notification.AppId != Windows.ApplicationModel.Package.Current.Id.FamilyName + "!App") return;
        var dispatch = _router.Receive(notification);
        if (dispatch is not null) DispatchMessage(dispatch);
        if (_router.HasPending) _merge.Start();
        else _merge.Stop();
    }

    private void DispatchMessage(MessageDispatch dispatch)
    {
        if (_closed) return;
        if (dispatch.ReplaceCurrent)
        {
            if (!_notification.Active || _current is null
                || !MessageRouter.Matches(_current, dispatch.Notification)) return;
            _current = dispatch.Notification;
            _notificationPanel.Show(dispatch.Notification);
            MeasureNotification();
            SetExpanded(true);
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
        SetExpanded(true);
        if (!AppWindow.IsVisible)
        {
            ApplyBounds();
            AppWindow.Show(false);
            NativeWindow.RemoveSystemBorder(_hwnd);
        }
        NativeWindow.ConfigureOverlay(_hwnd);
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
        _notification.Clear();
        _notificationPanel.Clear();
        _notificationPanel.Visibility = Visibility.Collapsed;
        if (animate && _isExpanded) SetExpanded(false);
        else HideIsland();
    }

    private void HideIsland()
    {
        _motion.Stop();
        _isExpanded = false;
        _width = CompactWidth;
        _height = CompactHeight;
        _notificationPanel.Visibility = Visibility.Collapsed;
        AppWindow.Hide();
        ApplyBounds();
    }
}
