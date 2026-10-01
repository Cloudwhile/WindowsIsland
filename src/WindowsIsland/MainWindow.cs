using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using WindowsIsland.Components;
using WindowsIsland.Services;

namespace WindowsIsland;

public sealed class MainWindow : Window
{
    private const double CompactWidth = 120, CompactHeight = 32;
    private const double NotificationWidth = 416, NotificationHeight = 196;
    private readonly Grid _root = new() { Background = IslandTheme.Brush(7, 8, 10), RequestedTheme = ElementTheme.Dark };
    private readonly NotificationService _notifications;
    private readonly TrayIcon _tray;
    private readonly NotificationPresentation _notification = new();
    private readonly NotificationPanel _notificationPanel = new()
    {
        Margin = new Thickness(24, 20, 24, 20), Visibility = Visibility.Collapsed
    };
    private readonly DispatcherQueueTimer _pulse, _motion;
    private readonly Stopwatch _animation = new();
    private readonly nint _hwnd;
    private bool _isExpanded, _closed, _polling;
    private double _width = CompactWidth, _height = CompactHeight;
    private double _fromWidth, _fromHeight, _toWidth, _toHeight;
    private int _tick;

    public MainWindow()
    {
        Title = "Windows Island";
        Content = _root;
        AutomationProperties.SetName(_root, "灵动岛");
        AutomationProperties.SetAutomationId(_root, "IslandSurface");
        AutomationProperties.SetAutomationId(_notificationPanel, "NotificationContent");
        _root.Children.Add(_notificationPanel);
        _notifications = new NotificationService(DispatcherQueue);
        _notifications.Received += ShowNotification;

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
        _tray = new TrayIcon(_hwnd,
            () => DispatcherQueue.TryEnqueue(Close),
            () => DispatcherQueue.TryEnqueue(async () => await RequestNotificationAccessAsync()));
        _notifications.AccessChanged += access =>
        {
            if (_closed) return;
            _tray.UpdateAccess(access);
            if (access != NotificationAccess.Allowed) DismissNotification();
        };

        _motion = DispatcherQueue.CreateTimer();
        _motion.Interval = TimeSpan.FromMilliseconds(16);
        _motion.Tick += Animate;
        _pulse = DispatcherQueue.CreateTimer();
        _pulse.Interval = TimeSpan.FromMilliseconds(250);
        _pulse.Tick += Pulse;
        Closed += (_, _) =>
        {
            _closed = true;
            _pulse.Stop();
            _motion.Stop();
            _notifications.Dispose();
            _tray.Dispose();
        };
        ApplyBounds();
    }

    public async Task StartListeningAsync()
    {
        _pulse.Start();
        await _notifications.InitializeAsync();
    }

    private async Task RequestNotificationAccessAsync()
    {
        if (_closed) return;
        await _notifications.InitializeAsync();
        if (_closed || _notifications.Access != NotificationAccess.Denied) return;
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-notifications")); }
        catch (Exception) { _tray.UpdateAccess(NotificationAccess.Unavailable); }
    }

    private void SetExpanded(bool expanded)
    {
        if (_closed || _isExpanded == expanded) return;
        _isExpanded = expanded;
        _notificationPanel.Visibility = Visibility.Collapsed;
        _fromWidth = _width; _fromHeight = _height;
        _toWidth = expanded ? NotificationWidth : CompactWidth;
        _toHeight = expanded ? NotificationHeight : CompactHeight;
        _animation.Restart();
        _motion.Start();
    }

    private void Animate(DispatcherQueueTimer sender, object args)
    {
        var t = Math.Clamp(_animation.Elapsed.TotalMilliseconds / 320, 0, 1);
        var ease = 1 - Math.Pow(1 - t, 4);
        _width = _fromWidth + (_toWidth - _fromWidth) * ease;
        _height = _fromHeight + (_toHeight - _fromHeight) * ease;
        ApplyBounds();
        if (t < 1) return;
        _motion.Stop();
        if (!_isExpanded) { AppWindow.Hide(); return; }
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
        var scale = Math.Max(1, NativeWindow.GetDpiForWindow(_hwnd) / 96d);
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var area = display.WorkArea;
        var width = (int)Math.Round(_width * scale);
        var height = (int)Math.Round(_height * scale);
        var x = area.X + (area.Width - width) / 2;
        var y = area.Y + (int)(12 * scale);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        NativeWindow.RemoveSystemBorder(_hwnd);
        NativeWindow.Round(_hwnd, width, height, (int)((_isExpanded ? 30 : CompactHeight / 2) * scale));
    }

    private async void Pulse(DispatcherQueueTimer sender, object args)
    {
        if (_closed) return;
        if (_notification.Expired) DismissNotification();
        if (++_tick % 4 != 0 || _polling) return;
        _tray.EnsureAdded();
        _polling = true;
        try
        {
            await _notifications.PollAsync();
            if (!_closed && _isExpanded && !_motion.IsRunning) ApplyBounds();
        }
        finally { _polling = false; }
    }

    private void ShowNotification(IslandNotification notification)
    {
        if (_closed) return;
        _notification.Show();
        _notificationPanel.Show(notification);
        if (!AppWindow.IsVisible)
        {
            ApplyBounds();
            AppWindow.Show(false);
        }
        SetExpanded(true);
    }

    private void DismissNotification()
    {
        _notification.Clear();
        _notificationPanel.Clear();
        _notificationPanel.Visibility = Visibility.Collapsed;
        SetExpanded(false);
    }
}
