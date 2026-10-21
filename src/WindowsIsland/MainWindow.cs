using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WindowsIsland.Components;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland;

public sealed partial class MainWindow : Window
{
    private const double MinimalWidth = 120, MinimalHeight = 32, SurfaceMargin = 12;
    private readonly IslandSurface _root;
    private readonly Grid _host = new();
    private readonly Grid _surfaceLayer;
    private readonly PluginManager _plugins;
    private readonly PluginLibrary _pluginLibrary;
    private readonly SettingsStore _settings;
    private readonly UpdateCheckService _updates;
    private SettingsWindow? _settingsWindow;
    private readonly MessageRouter _router = new();
    private readonly GameModePolicy _gameMode = new();
    private readonly TrayIcon _tray;
    private readonly IslandScheduler _scheduler = new();
    private readonly IslandContent _islandContent;
    private readonly NotificationPanel _notificationPanel = new()
    {
        Visibility = Visibility.Collapsed
    };
    private readonly DispatcherQueueTimer _pulse, _expiry, _merge;
    private readonly IslandMotion _motion;
    private readonly NotificationInput _input;
    private readonly PanelDismissMonitor _panelDismiss;
    private readonly nint _hwnd;
    private bool _surfaceVisible, _closed, _polling, _started, _exiting, _openingSource;
    private double _width = MinimalWidth, _height = MinimalHeight;
    private double _scale = 1;
    private RectInt32 _workArea;
    private RectInt32? _lastBounds;
    private int _gameModeRevision;
    private IslandNotification? _current => _scheduler.Message;
    private readonly bool _verification;
    private bool _showUpdateResult;
    private NotificationPosition _position;
    private AppSettings _appliedSettings;

    internal MainWindow(SettingsStore settings, bool verification = false, bool showUpdateResult = false)
    {
        _settings = settings;
        _updates = new UpdateCheckService(settings);
        _appliedSettings = settings.Current;
        _position = settings.Current.Position;
        _verification = verification;
        _showUpdateResult = showUpdateResult;
        Title = "Windows Island";
        _islandContent = new IslandContent(_notificationPanel);
        _root = new IslandSurface(_islandContent, AppWindow.Id)
        {
            Width = _width, Height = _height
        };
        _root.ApplyAppearance(settings.Current);
        _surfaceLayer = new Grid
        {
            Width = _width, Height = _height, Margin = new Thickness(SurfaceMargin),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top
        };
        _surfaceLayer.Children.Add(_root);
        _host.Children.Add(_surfaceLayer);
        Content = _host;
        SystemBackdrop = new OverlayBackdrop();
        _root.Language = Localization.Language;
        LocalizedUI.Label(_root, "AppName");
        AutomationProperties.SetAutomationId(_root, "IslandSurface");
        AutomationProperties.SetAutomationId(_notificationPanel, "NotificationContent");
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _input = new NotificationInput(_hwnd, right => DispatcherQueue.TryEnqueue(() => HandleIslandInput(right)));
        _panelDismiss = new PanelDismissMonitor(() => DispatcherQueue.TryEnqueue(DismissPinnedPanel),
            delta => DispatcherQueue.TryEnqueue(() => ScrollPinnedPanel(delta)));
        _islandContent.Panel.Dismissed += DismissPinnedPanel;
        _islandContent.Panel.Interacted += HandlePinnedInteraction;
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
        _plugins = BuiltInPlugins.Create(new(DispatcherQueue, _hwnd, verification), settings);
        var pluginPaths = new PluginPaths(verification ? Path.Combine(AppContext.BaseDirectory, "verification-plugins") : null);
        _plugins.ConfigureDirectories(pluginPaths);
        _pluginLibrary = new PluginLibrary(_plugins, pluginPaths);
        _updates.Changed += OnUpdateCheckChanged;
        _plugins.Received += ReceiveNotification;
        _plugins.PinnedChanged += ReceivePinned;
        _tray = new TrayIcon(_hwnd,
            () => DispatcherQueue.TryEnqueue(ExitApplication),
            () => DispatcherQueue.TryEnqueue(OpenSettings),
            () => DispatcherQueue.TryEnqueue(OpenSettings));
        _settings.Changed += OnSettingsChanged;
        _plugins.AccessChanged += access =>
        {
            if (_closed) return;
            _tray.UpdateAccess(access, _settings.Current.SystemNotifications);
            if (access != NotificationAccess.Allowed)
            {
                _router.ClearSystemHistory();
                if (_current?.Source == NotificationSource.SystemNotification) DismissNotification(animate: false);
            }
        };

        _motion = new IslandMotion(_surfaceLayer, _islandContent, SurfaceMargin,
            () => _settings.Current.Animations, () => _settings.Current.OpacityTransitions, _position);
        _motion.Settled += FinishPresentation;
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
            if (_closed) return;
            if (_scheduler.Tick()) PresentSchedule();
            if (!_scheduler.HasContent) _expiry.Stop();
            if (AppWindow.IsVisible) NativeWindow.EnsureTopmost(_hwnd);
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
            _updates.Dispose();
            _updates.Changed -= OnUpdateCheckChanged;
            _pluginLibrary.Dispose();
            _pulse.Stop();
            _motion.Dispose();
            _root.Dispose();
            _input.Dispose();
            _panelDismiss.Dispose();
            _expiry.Stop();
            _merge.Stop();
            _router.Clear();
            _plugins.Received -= ReceiveNotification;
            _plugins.PinnedChanged -= ReceivePinned;
            _plugins.Dispose();
            _tray.Dispose();
        };
        RefreshDisplayMetrics();
        ApplyBounds();
    }

    public async Task StartListeningAsync()
    {
        if (_started || _closed) return;
        _started = true;
        await _pluginLibrary.Catalog.LoadAsync();
        if (_closed) return;
        if (!_verification) _ = _updates.StartOnce();
        await PollGameModeAsync();
        if (_closed) return;
        _pulse.Start();
        await _plugins.StartAsync();
    }

    internal void OpenSettings()
    {
        if (_closed) return;
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settings, _updates,
                () => new SettingsSnapshot(_plugins.Access, AppInitialization.HasIdentity, _plugins.Snapshot(), _gameMode.Reason),
                InitializeApplicationAsync, RequestNotificationAccessAsync, PreviewNotification, ExitApplication, _showUpdateResult, _verification,
                _plugins.RetryAsync, _plugins.SetEnabledAsync, RemovePluginAsync, _pluginLibrary);
            _showUpdateResult = false;
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Activate();
    }

    private void OnUpdateCheckChanged()
    {
        if (!_verification && !_closed && _updates.State.Busy) _ = _pluginLibrary.Catalog.RefreshAsync();
    }

    private async Task InitializeApplicationAsync()
    {
        var appId = await AppInitialization.InitializeAsync();
        if (!AppInitialization.HasIdentity) await ((App)Application.Current).RestartRegisteredAsync(appId);
    }

    private async Task RemovePluginAsync(string id)
    {
        var removal = _plugins.RemoveAsync(id);
        if (_current is not null && !_plugins.Allows(_current)) DismissNotification(animate: false);
        await removal;
    }

    private async void PreviewNotification()
    {
        if (_closed) return;
        byte[]? icon = null;
        try { icon = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Icons", "LOGO.png")); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (_closed) return;
        ShowNotification(new IslandNotification(0, DateTimeOffset.Now, "Windows Island", Localization.Get("PreviewTitle"), Localization.Get("PreviewBody"), icon));
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        if (_closed) return;
        var previous = _appliedSettings;
        _appliedSettings = settings;
        if (previous.Acrylic != settings.Acrylic || previous.BackgroundOpacity != settings.BackgroundOpacity)
            _root.ApplyAppearance(settings);
        if (previous.OpacityTransitions != settings.OpacityTransitions) _motion.RefreshOpacityTransitions();
        Diagnostics.Store?.SetEnabled(settings.LocalDiagnostics);
        if (previous.Language != settings.Language)
        {
            App.ApplyLanguage(settings.Language);
            _root.Language = Localization.Language;
            _tray.UpdateAccess(_plugins.Access, settings.SystemNotifications);
            if (_current is not null)
            {
                if (_current is { Id: 0, AppName: "Windows Island", AppId: null })
                {
                    _scheduler.MessageUpdated(_current with { Title = Localization.Get("PreviewTitle"), Body = Localization.Get("PreviewBody") });
                }
                PresentSchedule(force: true);
            }
        }
        if (previous.MuteDuringGames != settings.MuteDuringGames || previous.MuteDuringFullScreen != settings.MuteDuringFullScreen)
            RefreshGameMode();
        _router.ApplySettings(settings);
        if (!_router.HasPending) _merge.Stop();
        if (_current is not null && !settings.Allows(_current)) DismissNotification(animate: false);
        _ = _plugins.ApplySettingsAsync(settings);
        _tray.UpdateAccess(_plugins.Access, settings.SystemNotifications);
        if (_position != settings.Position)
        {
            _input.Hide();
            _motion.Reset();
            _position = settings.Position;
            _motion.SetPosition(_position);
            PresentSchedule(force: true);
        }
        if (previous.Animations && !settings.Animations)
        {
            PresentSchedule(animate: false, force: true);
        }
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
        if (_plugins.Access == NotificationAccess.Allowed)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-notifications"));
            return;
        }
        await _plugins.RequestAccessAsync();
        if (_closed || _plugins.Access != NotificationAccess.Denied) return;
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-notifications")); }
        catch (Exception error) { Diagnostics.Capture(DiagnosticArea.Notifications, error, "OpenAccessSettings"); _tray.UpdateAccess(NotificationAccess.Unavailable); }
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

    private async void Pulse(DispatcherQueueTimer sender, object args)
    {
        if (_closed) return;
        _tray.EnsureAdded();
        if (_polling) return;
        _polling = true;
        try
        {
            await PollGameModeAsync();
            if (_closed) return;
            await _plugins.PollAsync();
            if (!_closed && _surfaceVisible && !_motion.IsRunning)
            {
                RefreshDisplayMetrics();
                ApplyBounds();
                UpdateInputRegion(_width, _height);
            }
        }
        finally { _polling = false; }
    }

    private async Task PollGameModeAsync()
    {
        var revision = _gameModeRevision;
        var settings = _settings.Current;
        var state = settings.MuteDuringGames || settings.MuteDuringFullScreen
            ? await Task.Run(GameModeDetector.ReadState) : UserNotificationState.Unknown;
        if (!_closed && revision == _gameModeRevision) ApplyGameMode(state);
    }

    private bool RefreshGameMode()
    {
        if (_closed) return true;
        _gameModeRevision++;
        var settings = _settings.Current;
        return ApplyGameMode(settings.MuteDuringGames || settings.MuteDuringFullScreen
            ? GameModeDetector.ReadState() : UserNotificationState.Unknown);
    }

    private bool ApplyGameMode(UserNotificationState state)
    {
        var settings = _settings.Current;
        var wasMuted = _gameMode.IsMuted;
        if (_gameMode.Update(settings.MuteDuringGames, settings.MuteDuringFullScreen, state))
        {
            _tray.SetMuteReason(_gameMode.Reason);
            if (_gameMode.IsMuted && !wasMuted)
            {
                _router.DiscardPending();
                _merge.Stop();
            }
            _scheduler.SetMuted(_gameMode.IsMuted);
            PresentSchedule(animate: false);
        }
        return _gameMode.IsMuted;
    }

    private bool AllowsNotification(IslandNotification notification)
    {
        if (_closed || !_plugins.Allows(notification)) return false;
        if (_verification && notification.Source == NotificationSource.SystemNotification
            && notification.AppId != Windows.ApplicationModel.Package.Current.Id.FamilyName + "!App") return false;
        return true;
    }

    private void ReceiveNotification(IslandNotification notification)
    {
        if (!AllowsNotification(notification)) return;
        RefreshGameMode();
        var dispatch = _router.Receive(notification);
        if (_gameMode.IsMuted)
        {
            _router.DiscardPending();
            _merge.Stop();
            return;
        }
        if (dispatch is not null) DispatchMessage(dispatch);
        if (_router.HasPending) _merge.Start();
        else _merge.Stop();
    }

    private void DispatchMessage(MessageDispatch dispatch)
    {
        if (!AllowsNotification(dispatch.Notification)) return;
        if (dispatch.ReplaceCurrent)
        {
            if (RefreshGameMode()) return;
            if (_current is null
                || !MessageRouter.Matches(_current, dispatch.Notification)) return;
            _scheduler.MessageUpdated(dispatch.Notification);
            PresentSchedule();
            if (AppWindow.IsVisible) NativeWindow.EnsureTopmost(_hwnd);
            return;
        }
        ShowNotification(dispatch.Notification);
    }

    private void ShowNotification(IslandNotification notification)
    {
        if (RefreshGameMode()) return;
        _scheduler.MessageArrived(notification);
        PresentSchedule(newMessage: true);
    }

}
