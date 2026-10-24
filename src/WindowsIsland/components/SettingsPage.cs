using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WindowsIsland.Services;
using WindowsIsland.Plugins;

namespace WindowsIsland.Components;

internal sealed record SettingsSnapshot(NotificationAccess Access, bool Initialized, IReadOnlyList<NotificationPluginStatus> Sources,
    NotificationMuteReason MuteReason = NotificationMuteReason.None);

internal sealed class SettingsPage : Grid, IDisposable
{
    private readonly SettingsStore _store;
    private readonly Func<SettingsSnapshot> _snapshot;
    private readonly InfoBar _notice = new() { IsOpen = true, IsClosable = false };
    private readonly ProgressRing _progress = new() { Width = 20, Height = 20, Visibility = Visibility.Collapsed };
    private readonly IconActionButton _initialize = new(Symbol.Download, "ActionInitialize", "InitializeApplication");
    private readonly IconActionButton _permission = new(Symbol.Permissions, "ActionAllowAccess", "RequestNotificationAccess");
    private readonly IconActionButton _complete = new(Symbol.Accept, "ActionCompleteSetup", "CompleteSetup");
    private readonly IconActionButton _installSettings = new(Symbol.Setting, "ActionOpenSystemSettings", "OpenInstallationSettings");
    // Page-specific toolbar: preview where notification appearance matters, refresh where status is shown.
    private readonly IconActionButton _previewButton = new(0xE7E7, "ActionPreview", "PreviewNotification");
    private readonly IconActionButton _refreshButton = new(Symbol.Refresh, "ActionRefreshStatus", "RefreshSettings");
    private static readonly HashSet<string> PreviewSections = ["sources", "appearance"], RefreshSections = ["overview", "setup", "sources", "plugins"];
    private readonly SettingsRow _initializationRow, _permissionRow;
    private readonly SettingsSources _sources;
    private readonly SettingsPluginLibrary? _pluginLibrary;
    private readonly SettingsRow _animationsRow;
    private readonly SettingsAppearance _appearance;
    private readonly SettingsRow _gameModeRow, _fullScreenRow;
    private readonly SettingsRow _startupRow;
    private readonly ToggleSwitch _startup;
    private readonly WindowsSettingsService _windowsSettings;
    private readonly DoNotDisturbSettings _doNotDisturbSettings;
    private readonly NotificationAppearancePreview _appearancePreview;
    private readonly NotificationPositionPicker _position = new();
    private readonly SettingsLanguagePicker _language = new();
    private readonly SettingsHeader _header;
    private readonly SettingsSetupFlow _setup;
    private readonly SettingsOverview _overview;
    private readonly IconActionButton _setupCheck = new(Symbol.Refresh, "ActionRefreshStatus", "VerifySetupStatus");
    private readonly SettingsNavigation _navigation = new();
    private readonly SettingsUpdates _updates;
    private readonly UpdateCheckService _updateChecks;
    private readonly SettingsDiagnostics _diagnostics;
    private readonly ScrollViewer _content = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Top
    };
    private readonly Dictionary<string, (string Title, FrameworkElement Content)> _sections = [];
    private readonly ToggleSwitch _animations, _muteDuringGames, _muteDuringFullScreen;
    private readonly List<Control> _actions = [];
    private bool _busy, _syncing, _failed, _cancelled, _disposed;
    private string _cancelledKey = "StartupPermissionCancelled";
    private string _selectedSection = "overview";
    private SettingsDetail? _detail;
    private double _returnOffset;
    private readonly Dictionary<string, double> _offsets = [];
    private Control? _returnFocus;
    private Control? _lastContentFocus;
    private Control? _pendingFocus;
    private RoutedEventHandler? _pendingFocusLoaded;
    private long _navigationRevision;
    private string? _operationSection;
    private string _errorKey = "ErrorSettingsSave";
    private StartupState _startupState;
    private bool _windowsReading, _windowsAvailable;
    private int _windowsRevision;

    public SettingsPage(SettingsStore store, UpdateCheckService updates, Func<SettingsSnapshot> snapshot, Func<Task> initialize,
        Func<Task> requestAccess, Action preview, Action close, Action exit, bool openUpdates = false, bool verification = false,
        Func<Task<string?>>? pickDiagnosticFile = null, Func<string, Task>? retrySource = null,
        Func<string, bool, Task>? setSourceEnabled = null, Func<string, Task>? removePlugin = null,
        PluginLibrary? pluginLibrary = null, Func<CancellationToken, Task<string?>>? pickPluginFile = null, Func<Task>? rescanPlugins = null,
        Func<string, Task<PluginSettingsModel?>>? pluginSettings = null)
    {
        _store = store;
        _updateChecks = updates;
        AutomationProperties.SetAutomationId(_content, "SettingsContentScroll");
        _content.GotFocus += OnContentFocus;
        retrySource ??= _ => Task.CompletedTask;
        _snapshot = snapshot;
        _windowsSettings = new WindowsSettingsService(verification);
        var layout = new Grid { MaxWidth = 960 };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        _refreshButton.Click += async (_, _) =>
        {
            await RefreshSourcesAsync();
        };
        async Task RefreshSourcesAsync()
        {
            if (rescanPlugins is not null) await RunAsync(rescanPlugins, "ErrorRefreshPlugins");
            else { _failed = _cancelled = false; Refresh(); }
        }
        _previewButton.Click += (_, _) => preview();
        _header = new SettingsHeader(_progress, _previewButton, _refreshButton);
        _header.BackRequested += ReturnFromDetail;
        layout.Children.Add(_header);
        _notice.Margin = new Thickness(SettingsTokens.PagePadding, 0, SettingsTokens.PagePadding, 12);
        Grid.SetRow(_notice, 1);
        layout.Children.Add(_notice);
        _initializationRow = new SettingsRow(Symbol.Download, "Initialization", _initialize);
        _permissionRow = new SettingsRow(Symbol.Permissions, "NotificationAccess", _permission);
        _setup = new SettingsSetupFlow(_initializationRow, _permissionRow, _setupCheck, _complete, new PortableRegistrationSettings(exit, verification));
        _setupCheck.Click += async (_, _) => await RefreshSourcesAsync();
        AddSection("setup", "Setup", _setup);
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
        _animations = Switch("Animations", "AnimationsToggle", settings => settings.Animations,
            (settings, value) => settings with { Animations = value });
        _animationsRow = new SettingsRow(Symbol.Play, "Animations", _animations);
        _position.Selected += position =>
        {
            try { _store.Save(_store.Current with { Position = position }); _failed = false; Refresh(); }
            catch (Exception error) { Diagnostics.Capture(DiagnosticArea.Settings, error); Trace.WriteLine(error); ShowError("ErrorPositionSave"); Refresh(); }
        };
        _language.LanguageSelected += language =>
        {
            try { _store.Save(_store.Current with { Language = language }); _failed = false; Refresh(); }
            catch (Exception error) { Diagnostics.Capture(DiagnosticArea.Settings, error); Trace.WriteLine(error); ShowError("ErrorSettingsSave"); Refresh(); }
        };
        _actions.Add(_language.Input);
        _appearance = new SettingsAppearance(store, update =>
        {
            try { _store.Save(update(_store.Current)); _failed = false; Refresh(); }
            catch (Exception error) { Diagnostics.Capture(DiagnosticArea.Settings, error); Trace.WriteLine(error); ShowError("ErrorSettingsSave"); Refresh(); }
        }, _animationsRow);
        _appearancePreview = new NotificationAppearancePreview(store, _position, preview);
        AddSection("appearance", "Appearance", new SettingsSection("AppearanceBasics", new SettingsRow(Symbol.World, "Language", _language)),
            new SettingsSection("AppearancePosition", _appearancePreview), _appearance);
        _doNotDisturbSettings = new DoNotDisturbSettings(_windowsSettings.DoNotDisturb, (action, message) => RunAsync(action, message));
        _doNotDisturbSettings.OpenSettings.Click += async (_, _) => await RunAsync(async () =>
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:notifications")))
                throw new InvalidOperationException("Windows notification settings did not open.");
        }, "ErrorNotificationSettings");
        _sources = new SettingsSources(store, error =>
        {
            Diagnostics.Capture(DiagnosticArea.Settings, error, "SaveSourcePreference");
            ShowError("ErrorSettingsSave");
            Refresh();
        }, async id => { await retrySource(id); Refresh(); }, setSourceEnabled,
            async id => { if (removePlugin is not null) await removePlugin(id); Refresh(); }, pluginLibrary?.Catalog, pluginSettings, OpenDetail);
        _muteDuringGames = Switch("MuteDuringGames", "MuteDuringGamesToggle", settings => settings.MuteDuringGames,
            (settings, value) => settings with { MuteDuringGames = value });
        _gameModeRow = new SettingsRow(Symbol.Mute, "MuteDuringGames", _muteDuringGames, "GameModeStatus");
        _muteDuringFullScreen = Switch("MuteDuringFullScreen", "MuteDuringFullScreenToggle", settings => settings.MuteDuringFullScreen,
            (settings, value) => settings with { MuteDuringFullScreen = value });
        _fullScreenRow = new SettingsRow(Symbol.FullScreen, "MuteDuringFullScreen", _muteDuringFullScreen, "FullScreenStatus");
        if (pluginLibrary is not null) _pluginLibrary = new SettingsPluginLibrary(pluginLibrary, pickPluginFile ?? (_ => Task.FromResult<string?>(null)), OpenDetail, () => Navigate("diagnostics"), store);
        AddSection("plugins", "PluginManagement", _pluginLibrary ?? new StackPanel());
        AddSection("sources", "Sources", _sources);
        AddSection("behavior", "NotificationBehavior",
            new SettingsSection("NotificationBehavior", _gameModeRow, _fullScreenRow),
            new SettingsSection("NotificationDisplay", _doNotDisturbSettings),
            !verification && InstallationMode.IsPortable() ? new StackPanel() : new SettingsSection("General", _startupRow));
        _updates = new SettingsUpdates(store, updates, exit);
        AddSection("updates", "Updates", _updates);
        _diagnostics = new SettingsDiagnostics(store, pickDiagnosticFile ?? (() => Task.FromResult<string?>(null)));
        AddSection("diagnostics", "DiagnosticsTitle", _diagnostics);
        _overview = new SettingsOverview(() => SelectSection("setup"), () => Navigate("sources"), preview, () =>
        {
            Navigate("updates");
            _ = _updateChecks.CheckAsync();
        });
        AddSection("overview", "Overview", _overview);
        Grid.SetRow(_content, 2);
        layout.Children.Add(_content);
        _navigation.Content = layout;
        _navigation.SectionSelected += SelectSection;
        Children.Add(_navigation);
        _actions.AddRange([_initialize, _permission, _refreshButton, _setupCheck, _complete, _previewButton, _installSettings]);
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
        KeyDown += OnKeyDown;
        Refresh();
        Navigate(openUpdates ? "updates" : "overview");
    }

    private void Navigate(string section)
    {
        _navigation.Select(section);
        if (_selectedSection != section || _detail is not null || _content.Content is null) SelectSection(section);
    }

    private void AddSection(string id, string title, params UIElement[] rows)
    {
        var content = SettingsSection.Page(rows);
        _sections.Add(id, (title, content));
        AutomationProperties.SetAutomationId(content, "SettingsSection" + id);
    }

    private void SelectSection(string section)
    {
        if (_disposed) return;
        if (_selectedSection == section && _detail is null && ReferenceEquals(_content.Content, _sections[section].Content)) return;
        CancelPendingFocus();
        _navigationRevision++;
        if (_detail is null) _offsets[_selectedSection] = _content.VerticalOffset;
        _detail?.Close?.Invoke();
        _detail = null;
        _returnFocus = null;
        _lastContentFocus = null;
        _header.HasBack = false;
        if (_selectedSection != section && !_busy) _failed = _cancelled = false;
        _selectedSection = section;
        var selected = _sections[section];
        _header.Title = Localization.Get(selected.Title);
        _previewButton.Visibility = PreviewSections.Contains(section) ? Visibility.Visible : Visibility.Collapsed;
        _refreshButton.Visibility = RefreshSections.Contains(section) ? Visibility.Visible : Visibility.Collapsed;
        _content.Content = selected.Content;
        RestoreOffset(_offsets.GetValueOrDefault(section));
        Refresh();
    }

    private void OpenDetail(SettingsDetail detail)
    {
        if (_disposed) { detail.Close?.Invoke(); return; }
        CancelPendingFocus();
        _navigationRevision++;
        _detail?.Close?.Invoke();
        _returnOffset = _content.VerticalOffset;
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as Control;
        _returnFocus = focused is not null && BelongsToContent(focused, _content.Content) ? focused
            : _lastContentFocus is { IsLoaded: true } previous && BelongsToContent(previous, _content.Content) ? previous : null;
        _lastContentFocus = null;
        _detail = detail;
        _header.Title = detail.LocalizeTitle ? Localization.Get(detail.Title) : detail.Title;
        _header.HasBack = true;
        _previewButton.Visibility = _refreshButton.Visibility = Visibility.Collapsed;
        _content.Content = detail.Content;
        _content.ChangeView(null, 0, null, disableAnimation: true);
        DispatcherQueue.TryEnqueue(() => { if (!_disposed && _detail == detail) _header.FocusTitle(); });
        Refresh();
    }

    private void ReturnFromDetail()
    {
        var offset = _returnOffset;
        var focus = _returnFocus;
        SelectSection(_selectedSection);
        RestoreOffset(offset);
        RestoreFocus(focus, _sections[_selectedSection].Content);
    }

    private void RestoreOffset(double offset)
    {
        var content = _content.Content;
        var revision = _navigationRevision;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed && _detail is null && revision == _navigationRevision && ReferenceEquals(_content.Content, content))
                _content.ChangeView(null, offset, null, true);
        });
    }

    private void RestoreFocus(Control? target, FrameworkElement content)
    {
        CancelPendingFocus();
        if (target is null) return;
        var revision = _navigationRevision;
        _pendingFocus = target;
        void Apply()
        {
            if (!ReferenceEquals(_pendingFocus, target) || revision != _navigationRevision) return;
            if (_disposed || _detail is not null || !ReferenceEquals(_content.Content, content))
            {
                CancelPendingFocus();
                return;
            }
            if (!target.IsLoaded) return;
            var belongsToContent = BelongsToContent(target, content);
            CancelPendingFocus();
            if (belongsToContent && target.IsEnabled && target.Visibility == Visibility.Visible)
                target.Focus(FocusState.Programmatic);
        }
        void Queue() => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Apply);
        _pendingFocusLoaded = (_, _) => Queue();
        target.Loaded += _pendingFocusLoaded;
        Queue();
    }

    private void CancelPendingFocus()
    {
        if (_pendingFocus is { } target && _pendingFocusLoaded is { } loaded) target.Loaded -= loaded;
        _pendingFocus = null;
        _pendingFocusLoaded = null;
    }

    private void OnContentFocus(object sender, RoutedEventArgs args)
    {
        if (!_disposed && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is Control focused
            && BelongsToContent(focused, _content.Content)) _lastContentFocus = focused;
    }

    private static bool BelongsToContent(DependencyObject target, object? content)
    {
        for (DependencyObject? element = target; element is not null; element = VisualTreeHelper.GetParent(element))
            if (ReferenceEquals(element, content)) return true;
        return false;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Escape) return;
        if (_detail is not null) ReturnFromDetail();
        else if (_selectedSection == "setup") Navigate("overview");
        else return;
        args.Handled = true;
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
            catch (Exception error) { Diagnostics.Capture(DiagnosticArea.Settings, error); Trace.WriteLine(error); ShowError("ErrorSettingsSave"); Refresh(); }
        };
        _actions.Add(toggle);
        return toggle;
    }

    private async Task RunAsync(Func<Task> action, string message, bool showInstallSettings = false)
    {
        if (_busy || _disposed) return;
        _busy = true;
        var origin = _operationSection = _selectedSection;
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
        catch (OperationCanceledException)
        {
            if (!_disposed)
            {
                _cancelled = true;
                _cancelledKey = message is "ErrorInitialize" or "ErrorAccess" ? "InitializationPermissionCancelled"
                    : message == "ErrorStartupSettings" ? "StartupPermissionCancelled" : "SettingsOperationCancelled";
            }
        }
        catch (Exception error)
        {
            Diagnostics.Capture(DiagnosticArea.Settings, error, message); Trace.WriteLine(error);
            if (!_disposed) ShowError(InitializationRegistration.IsSideLoadingDisabled(error) ? "NeedsDeveloperMode" : message, showInstallSettings, origin);
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
        try { _store.Save(_store.Current with { SetupCompleted = true }); Navigate("overview"); }
        catch (Exception error) { Diagnostics.Capture(DiagnosticArea.Settings, error); Trace.WriteLine(error); ShowError("ErrorSetupSave"); }
    }

    private void ShowError(string message, bool showInstallSettings = false, string? section = null)
    {
        _failed = true;
        _operationSection = section ?? _selectedSection;
        _errorKey = message;
        if (_operationSection != _selectedSection) return;
        _header.Status = Localization.Get("NeedsAttention");
        _notice.Visibility = Visibility.Visible;
        _notice.Title = Localization.Get("Incomplete");
        _notice.Message = Localization.Get(message);
        _notice.Severity = InfoBarSeverity.Error;
        _notice.ActionButton = showInstallSettings ? _installSettings : null;
    }

    private void RefreshLanguage()
    {
        _header.Title = _detail is { } detail ? detail.LocalizeTitle ? Localization.Get(detail.Title) : detail.Title
            : Localization.Get(_sections[_selectedSection].Title);
        Refresh();
    }

    private void OnSettingsChanged(AppSettings _) => Refresh();

    public void Refresh()
    {
        if (_disposed) return;
        if (_selectedSection == "plugins") _pluginLibrary?.Refresh();
        if (_selectedSection == "diagnostics") _diagnostics.Refresh();
        if (_selectedSection == "appearance") { _appearance.Refresh(!_busy); _appearancePreview.Refresh(!_busy); }
        var state = _snapshot();
        var settings = _store.Current;
        _syncing = true;
        try
        {
            if (_selectedSection == "sources") _sources.Refresh(state.Sources, _busy);
            if (_selectedSection == "overview") _overview.Refresh(state, settings, _busy, _updateChecks.State);
            if (_detail?.IsCurrent?.Invoke() == false) ReturnFromDetail();
            _detail?.Refresh?.Invoke();
            if (_selectedSection == "appearance")
            {
                _animations.IsOn = settings.Animations;
                _position.Select(settings.Position);
                _language.Select(settings.Language);
                _animationsRow.Status = Localization.Get(settings.Animations ? "On" : "Off");
            }
            if (_selectedSection == "behavior")
            {
                _muteDuringGames.IsOn = settings.MuteDuringGames;
                _muteDuringFullScreen.IsOn = settings.MuteDuringFullScreen;
                _startup.IsOn = _startupState == StartupState.Enabled;
            }
        }
        finally { _syncing = false; }
        foreach (var action in _actions) action.IsEnabled = !_busy;
        if (_selectedSection == "behavior")
        {
            _startup.IsEnabled = !_busy && _windowsAvailable;
            _doNotDisturbSettings.SetEnabled(!_busy);
            _startupRow.Status = Localization.Get(!_windowsAvailable ? "Unavailable" : _startupState switch
            {
                StartupState.Enabled => "On",
                StartupState.Blocked => "StartupDisabledInWindows",
                StartupState.OtherInstallation => "StartupOtherInstallation",
                _ => "Off"
            });
            if (!_busy) _ = RefreshWindowsAsync();
            _gameModeRow.Status = Localization.Get(!settings.MuteDuringGames ? "Off"
                : state.MuteReason.HasFlag(NotificationMuteReason.Game) ? "GameModeMuted" : "On");
            _fullScreenRow.Status = Localization.Get(!settings.MuteDuringFullScreen ? "Off"
                : state.MuteReason.HasFlag(NotificationMuteReason.FullScreen) ? "FullScreenMuted" : "On");
        }
        if (_selectedSection == "appearance") _position.SetEnabled(!_busy);
        var ready = !settings.SystemNotifications || state.Initialized && state.Access == NotificationAccess.Allowed;
        if (_selectedSection == "setup")
        {
            _initializationRow.Status = Localization.Get(state.Initialized ? "Complete" : AppInitialization.NeedsDeveloperMode
                ? "NeedsDeveloperMode" : settings.SystemNotifications ? "Pending" : "Optional");
            _permissionRow.Status = Localization.Get(state.Access switch
            {
                NotificationAccess.Allowed => "Allowed",
                NotificationAccess.Denied => "Denied",
                NotificationAccess.NeedsRegistration => "NeedsSetup",
                NotificationAccess.Unavailable => "Unavailable",
                _ => "AwaitingPermission"
            });
            _permissionRow.Tone = state.Access switch
            {
                NotificationAccess.Allowed => StatusTone.Success,
                NotificationAccess.Denied or NotificationAccess.Unavailable => StatusTone.Critical,
                _ => StatusTone.Neutral
            };
            _initialize.IsEnabled = !_busy && !state.Initialized && settings.SystemNotifications;
            _permission.IsEnabled = !_busy && state.Initialized && settings.SystemNotifications;
            _permission.SetAction(state.Access == NotificationAccess.Allowed ? Symbol.Setting : Symbol.Permissions,
                state.Access == NotificationAccess.Allowed ? "ActionManageAccess" : "ActionAllowAccess");
            _setup.Refresh(state, settings, _busy);
            _complete.IsEnabled = !_busy && ready;
            _complete.SetAction(Symbol.Accept, "SetupFinish");
        }
        var running = state.Sources.Count(source => SourceStatus.IsRunning(source.StatusKey));
        var summary = "";
        if (_busy && _operationSection == _selectedSection)
        {
            _notice.Visibility = Visibility.Visible;
            _notice.Severity = InfoBarSeverity.Informational;
            _header.Status = _notice.Title = Localization.Get("Busy");
            _notice.Message = Localization.Get("PleaseWait");
            return;
        }
        if (_cancelled && _operationSection == _selectedSection)
        {
            _header.Status = summary;
            _notice.Visibility = Visibility.Visible;
            _notice.Title = Localization.Get("Cancelled");
            _notice.Message = Localization.Get(_cancelledKey);
            _notice.Severity = InfoBarSeverity.Informational;
            return;
        }
        if (_failed && _operationSection == _selectedSection)
        {
            _notice.Visibility = Visibility.Visible;
            _notice.Severity = InfoBarSeverity.Error;
            _header.Status = Localization.Get("NeedsAttention");
            _notice.Title = Localization.Get("Incomplete");
            _notice.Message = Localization.Get(_errorKey);
            return;
        }
        _header.Status = summary;
        var explainSetup = !ready && _selectedSection == "setup" && _detail is null;
        _notice.Visibility = explainSetup ? Visibility.Visible : Visibility.Collapsed;
        _notice.ActionButton = null;
        _notice.Title = explainSetup ? Localization.Get("IncompleteSetup") : "";
        _notice.Message = explainSetup ? Localization.Get(state.Initialized ? "GrantAccessOrDisable" : "InitializeThenGrantAccess") : "";
        _notice.Severity = InfoBarSeverity.Informational;
    }

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
            Diagnostics.Capture(DiagnosticArea.WindowsSettings, error, "ReadStartup");
            if (!_disposed && !_busy) { _windowsAvailable = false; _startup.IsEnabled = false; _startupRow.Status = Localization.Get("ErrorReadWindowsSettings"); }
        }
        finally { _syncing = false; _windowsReading = false; }
        await _doNotDisturbSettings.RefreshAsync();
    }

    public void Dispose()
    {
        _disposed = true;
        _navigationRevision++;
        CancelPendingFocus();
        KeyDown -= OnKeyDown;
        _detail?.Close?.Invoke();
        _detail = null;
        _returnFocus = null;
        _lastContentFocus = null;
        _content.GotFocus -= OnContentFocus;
        _header.BackRequested -= ReturnFromDetail;
        _store.Changed -= OnSettingsChanged;
        _updates.Dispose();
        _diagnostics.Dispose();
        _sources.Dispose();
        _pluginLibrary?.Dispose();
        _appearance.Dispose();
        _appearancePreview.Dispose();
        _doNotDisturbSettings.Dispose();
        _navigation.Dispose();
    }
}
