using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed record SettingsSnapshot(NotificationAccess Access, bool Initialized, IReadOnlyCollection<string> ConnectedApps,
    string WeChatStatus, NotificationMuteReason MuteReason = NotificationMuteReason.None);

internal sealed class SettingsPage : Grid, IDisposable
{
    private readonly SettingsStore _store;
    private readonly Func<SettingsSnapshot> _snapshot;
    private readonly Action _close;
    private readonly InfoBar _notice = new() { IsOpen = true, IsClosable = false };
    private readonly ProgressRing _progress = new() { Width = 20, Height = 20, Visibility = Visibility.Collapsed };
    private readonly IconActionButton _initialize = new(Symbol.Download, "ActionInitialize", "InitializeApplication");
    private readonly IconActionButton _permission = new(Symbol.Permissions, "ActionAllowAccess", "RequestNotificationAccess");
    private readonly IconActionButton _complete = new(Symbol.Accept, "ActionCompleteSetup", "CompleteSetup");
    private readonly IconActionButton _installSettings = new(Symbol.Setting, "ActionOpenSystemSettings", "OpenInstallationSettings");
    private readonly SettingsRow _initializationRow, _permissionRow, _systemRow, _weChatRow, _telegramRow, _powerRow;
    private readonly SettingsRow _animationsRow;
    private readonly SettingsRow _gameModeRow, _fullScreenRow;
    private readonly SettingsRow _startupRow;
    private readonly ToggleSwitch _startup;
    private readonly WindowsSettingsService _windowsSettings;
    private readonly NotificationBannerSettings _bannerSettings;
    private readonly SettingsRow _positionRow;
    private readonly NotificationPositionPicker _position = new();
    private readonly SettingsLanguagePicker _language = new();
    private readonly SettingsHeader _header;
    private readonly SettingsSetup _setup;
    private readonly SettingsNavigation _navigation = new();
    private readonly SettingsUpdates _updates;
    private readonly ScrollViewer _content = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Top
    };
    private readonly Dictionary<string, (string Title, FrameworkElement Content)> _sections = [];
    private readonly ToggleSwitch _system, _weChat, _telegram, _power, _animations, _muteDuringGames, _muteDuringFullScreen;
    private readonly List<Control> _actions = [];
    private bool _busy, _syncing, _failed, _cancelled, _disposed;
    private bool? _ready;
    private string _selectedSection = "setup";
    private string _errorKey = "ErrorSettingsSave";
    private StartupState _startupState;
    private bool _windowsReading, _windowsAvailable;
    private int _windowsRevision;

    public SettingsPage(SettingsStore store, Func<SettingsSnapshot> snapshot, Func<Task> initialize,
        Func<Task> requestAccess, Action preview, Action close, Action exit, bool openUpdates = false, bool verification = false)
    {
        _store = store;
        _snapshot = snapshot;
        _close = close;
        _windowsSettings = new WindowsSettingsService(verification);
        var layout = new Grid { MaxWidth = 720 };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        var refresh = new IconActionButton(Symbol.Refresh, "ActionRefreshStatus", "RefreshSettings");
        refresh.Click += (_, _) => { _failed = _cancelled = false; Refresh(); };
        var previewButton = new IconActionButton(Symbol.Play, "ActionPreview", "PreviewNotification");
        previewButton.Click += (_, _) => preview();
        _header = new SettingsHeader(_progress, previewButton, refresh, _complete) { MaxWidth = 720 };
        layout.Children.Add(_header);
        _notice.Margin = new Thickness(16, 0, 16, 12);
        Grid.SetRow(_notice, 1);
        layout.Children.Add(_notice);
        _initializationRow = new SettingsRow(Symbol.Download, "Initialization", _initialize);
        _permissionRow = new SettingsRow(Symbol.Permissions, "NotificationAccess", _permission);
        _setup = new SettingsSetup(_initializationRow, _permissionRow);
        _startup = new ToggleSwitch { OnContent = null, OffContent = null, MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(_startup, "StartupToggle");
        LocalizedUI.Label(_startup, "LaunchAtStartup");
        LocalizedUI.Bind(_startup, () => ToolTipService.SetToolTip(_startup, Localization.Get("ActionConfigureStartup")));
        _startupRow = new SettingsRow(Symbol.Play, "LaunchAtStartup", _startup, "StartupStatus");
        _startup.Toggled += async (_, _) =>
        {
            if (_syncing || _disposed) return;
            var enabled = _startup.IsOn;
            _windowsRevision++;
            await RunAsync(async () =>
            {
                if (!await _windowsSettings.SetStartupAsync(enabled)) throw new OperationCanceledException();
                _startupState = await Task.Run(_windowsSettings.Startup.Read);
            }, "ErrorStartupSettings");
        };
        _actions.Add(_startup);
        AddSection("setup", "Setup", _setup, _startupRow);
        _animations = Switch("Animations", "AnimationsToggle", settings => settings.Animations,
            (settings, value) => settings with { Animations = value });
        _animationsRow = new SettingsRow(Symbol.Play, "Animations", _animations);
        _positionRow = new SettingsRow(Symbol.Map, "NotificationPosition", _position);
        _position.Selected += position =>
        {
            try { _store.Save(_store.Current with { Position = position }); _failed = false; Refresh(); }
            catch (Exception error) { Trace.WriteLine(error); ShowError("ErrorPositionSave"); Refresh(); }
        };
        _language.LanguageSelected += language =>
        {
            try { _store.Save(_store.Current with { Language = language }); _failed = false; Refresh(); }
            catch (Exception error) { Trace.WriteLine(error); ShowError("ErrorSettingsSave"); Refresh(); }
        };
        _actions.Add(_language.Input);
        AddSection("appearance", "Appearance", new SettingsRow(Symbol.World, "Language", _language), _positionRow, _animationsRow);
        _bannerSettings = new NotificationBannerSettings(_windowsSettings, (action, message) => RunAsync(action, message));
        _bannerSettings.OpenSettings.Click += async (_, _) => await RunAsync(async () =>
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:notifications")))
                throw new InvalidOperationException("Windows notification settings did not open.");
        }, "ErrorNotificationSettings");
        _system = Switch("SystemNotifications", "SystemNotificationsToggle", settings => settings.SystemNotifications,
            (settings, value) => settings with { SystemNotifications = value });
        _weChat = Switch("WeChat", "WeChatToggle", settings => settings.WeChat, (settings, value) => settings with { WeChat = value });
        _telegram = Switch("Telegram", "TelegramToggle", settings => settings.Telegram, (settings, value) => settings with { Telegram = value });
        _power = Switch("Power", "PowerToggle", settings => settings.Power, (settings, value) => settings with { Power = value });
        _systemRow = new SettingsRow(Symbol.Message, "SystemNotifications", _system);
        _weChatRow = new SettingsRow(Symbol.Contact, "WeChat", _weChat);
        _telegramRow = new SettingsRow(Symbol.Send, "Telegram", _telegram);
        _powerRow = new SettingsRow(new FontIcon { Glyph = char.ConvertFromUtf32(0xE7E8), FontSize = 20 }, "Power", _power);
        _muteDuringGames = Switch("MuteDuringGames", "MuteDuringGamesToggle", settings => settings.MuteDuringGames,
            (settings, value) => settings with { MuteDuringGames = value });
        _gameModeRow = new SettingsRow(Symbol.Mute, "MuteDuringGames", _muteDuringGames, "GameModeStatus");
        _muteDuringFullScreen = Switch("MuteDuringFullScreen", "MuteDuringFullScreenToggle", settings => settings.MuteDuringFullScreen,
            (settings, value) => settings with { MuteDuringFullScreen = value });
        _fullScreenRow = new SettingsRow(Symbol.FullScreen, "MuteDuringFullScreen", _muteDuringFullScreen, "FullScreenStatus");
        AddSection("sources", "Sources", _systemRow, _weChatRow, _telegramRow, _powerRow, _gameModeRow, _fullScreenRow,
            new SettingsSection("NotificationDisplay", _bannerSettings));
        _updates = new SettingsUpdates(store, exit);
        AddSection("updates", "Updates", _updates);
        Grid.SetRow(_content, 2);
        layout.Children.Add(_content);
        _navigation.Content = layout;
        _navigation.SectionSelected += SelectSection;
        Children.Add(_navigation);
        _actions.AddRange([_initialize, _permission, refresh, _complete, previewButton, _installSettings]);
        _initialize.Click += async (_, _) => await RunAsync(initialize, "ErrorInitialize", showInstallSettings: true);
        _permission.Click += async (_, _) => await RunAsync(requestAccess, "ErrorAccess");
        _installSettings.Click += async (_, _) => await RunAsync(async () =>
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:developers")))
                throw new InvalidOperationException("Windows settings did not open.");
        }, "ErrorSystemSettings");
        _complete.Click += (_, _) => Complete();
        _store.Changed += OnSettingsChanged;
        AutomationProperties.SetAutomationId(this, "SettingsPage");
        AutomationProperties.SetAutomationId(_notice, "SetupStatus");
        LocalizedUI.Bind(this, RefreshLanguage);
        Refresh();
        _navigation.Select(openUpdates ? "updates" : store.Current.SetupCompleted ? "sources" : "setup");
    }

    private void AddSection(string id, string title, params UIElement[] rows)
    {
        var content = new StackPanel { Margin = new Thickness(16, 0, 16, 24) };
        foreach (var row in rows) content.Children.Add(row);
        _sections.Add(id, (title, content));
        AutomationProperties.SetAutomationId(content, "SettingsSection" + id);
    }

    private void SelectSection(string section)
    {
        _selectedSection = section;
        var selected = _sections[section];
        _header.Title = Localization.Get(selected.Title);
        _content.Content = selected.Content;
        _content.ChangeView(null, 0, null, disableAnimation: true);
    }

    private ToggleSwitch Switch(string label, string id, Func<AppSettings, bool> read,
        Func<AppSettings, bool, AppSettings> update)
    {
        var toggle = new ToggleSwitch
        {
            IsOn = read(_store.Current), OnContent = null, OffContent = null, MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right
        };
        LocalizedUI.Label(toggle, label);
        AutomationProperties.SetAutomationId(toggle, id);
        toggle.Toggled += (_, _) =>
        {
            if (_syncing || _disposed) return;
            try { _store.Save(update(_store.Current, toggle.IsOn)); _failed = false; Refresh(); }
            catch (Exception error) { Trace.WriteLine(error); ShowError("ErrorSettingsSave"); Refresh(); }
        };
        _actions.Add(toggle);
        return toggle;
    }

    private async Task RunAsync(Func<Task> action, string message, bool showInstallSettings = false)
    {
        if (_busy || _disposed) return;
        _busy = true;
        _failed = false;
        _cancelled = false;
        _notice.ActionButton = null;
        _notice.Visibility = Visibility.Visible;
        _notice.Title = Localization.Get("Busy");
        _notice.Message = Localization.Get("PleaseWait");
        _notice.Severity = InfoBarSeverity.Informational;
        _progress.IsActive = true;
        _progress.Visibility = Visibility.Visible;
        Refresh();
        try { await action(); }
        catch (OperationCanceledException) { if (!_disposed) _cancelled = true; }
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
        catch (Exception error) { Trace.WriteLine(error); ShowError("ErrorSetupSave"); }
    }

    private void ShowError(string message, bool showInstallSettings = false)
    {
        _failed = true;
        _errorKey = message;
        _header.Status = Localization.Get("NeedsAttention");
        _notice.Visibility = Visibility.Visible;
        _notice.Title = Localization.Get("Incomplete");
        _notice.Message = Localization.Get(message);
        _notice.Severity = InfoBarSeverity.Error;
        _notice.ActionButton = showInstallSettings ? _installSettings : null;
    }

    private void RefreshLanguage()
    {
        _header.Title = Localization.Get(_sections[_selectedSection].Title);
        Refresh();
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
            _muteDuringGames.IsOn = settings.MuteDuringGames;
            _muteDuringFullScreen.IsOn = settings.MuteDuringFullScreen;
            _startup.IsOn = _startupState == StartupState.Enabled;
            _position.Select(settings.Position);
            _language.Select(settings.Language);
        }
        finally { _syncing = false; }
        foreach (var action in _actions) action.IsEnabled = !_busy;
        _startup.IsEnabled = !_busy && _windowsAvailable;
        _bannerSettings.SetEnabled(!_busy);
        _startupRow.Status = Localization.Get(!_windowsAvailable ? "Unavailable" : _startupState switch
        {
            StartupState.Enabled => "On",
            StartupState.Blocked => "StartupDisabledInWindows",
            StartupState.OtherInstallation => "StartupOtherInstallation",
            _ => "Off"
        });
        if (!_busy) _ = RefreshWindowsAsync();
        _position.SetEnabled(!_busy);
        _positionRow.Status = NotificationPlacement.Label(settings.Position);
        _initializationRow.Status = Localization.Get(state.Initialized ? "Complete" : settings.SystemNotifications ? "Pending" : "Optional");
        _permissionRow.Status = Localization.Get(state.Access switch
        {
            NotificationAccess.Allowed => "Allowed",
            NotificationAccess.Denied => "Denied",
            NotificationAccess.NeedsRegistration => "NeedsSetup",
            NotificationAccess.Unavailable => "Unavailable",
            _ => "AwaitingPermission"
        });
        _initialize.IsEnabled = !_busy && !state.Initialized && settings.SystemNotifications;
        _permission.IsEnabled = !_busy && state.Initialized && settings.SystemNotifications;
        _permission.SetAction(state.Access == NotificationAccess.Allowed ? Symbol.Setting : Symbol.Permissions,
            state.Access == NotificationAccess.Allowed ? "ActionManageAccess" : "ActionAllowAccess");
        var ready = !settings.SystemNotifications || state.Initialized && state.Access == NotificationAccess.Allowed;
        if (_ready != ready) _setup.IsExpanded = !ready;
        _ready = ready;
        _setup.Status = Localization.Get(ready ? "Ready" : "Pending");
        _complete.IsEnabled = !_busy && ready;
        _complete.Visibility = settings.SetupCompleted ? Visibility.Collapsed : Visibility.Visible;
        _complete.SetAction(Symbol.Accept, settings.SetupCompleted ? "ActionCloseSettings" : "ActionCompleteSetup");
        _systemRow.Status = Localization.Get(!settings.SystemNotifications ? "Off" : state.Access == NotificationAccess.Allowed ? "On" : "AwaitingPermission");
        _weChatRow.Status = settings.WeChat ? state.WeChatStatus : Localization.Get("Off");
        _telegramRow.Status = ClientStatus(settings.Telegram, state.ConnectedApps.Contains("telegram"));
        _powerRow.Status = Localization.Get(settings.Power ? "On" : "Off");
        _animationsRow.Status = Localization.Get(settings.Animations ? "On" : "Off");
        _gameModeRow.Status = Localization.Get(!settings.MuteDuringGames ? "Off"
            : state.MuteReason.HasFlag(NotificationMuteReason.Game) ? "GameModeMuted" : "GameModeMonitoring");
        _fullScreenRow.Status = Localization.Get(!settings.MuteDuringFullScreen ? "Off"
            : state.MuteReason.HasFlag(NotificationMuteReason.FullScreen) ? "FullScreenMuted" : "GameModeMonitoring");
        if (_busy)
        {
            _header.Status = _notice.Title = Localization.Get("Busy");
            _notice.Message = Localization.Get("PleaseWait");
            return;
        }
        if (_cancelled)
        {
            _header.Status = Localization.Get(ready ? "Ready" : "IncompleteSetup");
            _notice.Visibility = Visibility.Visible;
            _notice.Title = Localization.Get("Cancelled");
            _notice.Message = Localization.Get("StartupPermissionCancelled");
            _notice.Severity = InfoBarSeverity.Informational;
            return;
        }
        if (_failed)
        {
            _header.Status = Localization.Get("NeedsAttention");
            _notice.Title = Localization.Get("Incomplete");
            _notice.Message = Localization.Get(_errorKey);
            return;
        }
        _header.Status = Localization.Get(ready ? "Ready" : "IncompleteSetup");
        _notice.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        _notice.ActionButton = null;
        _notice.Title = Localization.Get(ready ? "Ready" : "IncompleteSetup");
        _notice.Message = Localization.Get(ready ? "SettingsSaved" : state.Initialized ? "GrantAccessOrDisable" : "InitializeThenGrantAccess");
        _notice.Severity = ready ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
    }

    private static string ClientStatus(bool enabled, bool connected) => Localization.Get(!enabled ? "Off" : connected ? "Connected" : "NotRunning");

    private async Task RefreshWindowsAsync()
    {
        if (_windowsReading || _disposed) return;
        _windowsReading = true;
        var revision = _windowsRevision;
        try
        {
            var state = await Task.Run(_windowsSettings.Startup.Read);
            if (_disposed || _busy || revision != _windowsRevision) return;
            _startupState = state;
            _windowsAvailable = true;
            _syncing = true;
            _startup.IsOn = state == StartupState.Enabled;
            _startup.IsEnabled = true;
            _startupRow.Status = Localization.Get(state switch
            {
                StartupState.Enabled => "On", StartupState.Blocked => "StartupDisabledInWindows",
                StartupState.OtherInstallation => "StartupOtherInstallation", _ => "Off"
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (!_disposed && !_busy) { _windowsAvailable = false; _startup.IsEnabled = false; _startupRow.Status = Localization.Get("ErrorReadWindowsSettings"); }
        }
        finally { _syncing = false; _windowsReading = false; }
        await _bannerSettings.RefreshAsync();
    }

    public void Dispose()
    {
        _disposed = true;
        _store.Changed -= OnSettingsChanged;
        _updates.Dispose();
        _bannerSettings.Dispose();
        _navigation.Dispose();
    }
}
