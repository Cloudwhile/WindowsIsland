using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Services;
using WindowsIsland.Plugins;

namespace WindowsIsland.Components;

internal sealed class SettingsOverview : StackPanel
{
    private readonly InfoBar _notice = new() { IsClosable = false };
    private readonly IconActionButton _setup = new(Symbol.Permissions, "ActionGoToSetup", "OverviewSetup");
    private readonly SettingsSummaryTile _sources = new(Symbol.Link, "OverviewRunningSources", "OverviewSourceCount");
    private readonly SettingsSummaryTile _version = new(Symbol.AllApps, "UpdateCurrentVersion", "OverviewVersion");
    private readonly Grid _metrics = new() { ColumnSpacing = SettingsTokens.ControlSpacing, RowSpacing = SettingsTokens.ControlSpacing };
    private readonly IconActionButton _preview;
    private readonly SettingsRow _sourceHealth;

    public SettingsOverview(Action setup, Action sources, Action preview, Action updates)
    {
        Spacing = SettingsTokens.RowPadding;
        _setup.Click += (_, _) => setup();
        _notice.ActionButton = _setup;
        AutomationProperties.SetAutomationId(_notice, "OverviewSystemStatus");
        Children.Add(_notice);
        Children.Add(SettingsSection.Heading("OverviewRuntime"));
        _metrics.ColumnDefinitions.Add(new ColumnDefinition());
        _metrics.ColumnDefinitions.Add(new ColumnDefinition());
        _metrics.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _metrics.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _metrics.Children.Add(_sources);
        _metrics.Children.Add(_version);
        Grid.SetColumn(_version, 1);
        _version.Value = ReleaseVersion.Current.Tag;
        Children.Add(_metrics);
        var sourceAction = new IconActionButton(Symbol.Message, "Sources", "OverviewManageSources");
        sourceAction.Click += (_, _) => sources();
        var inspectSources = new IconActionButton(0xE72A, "Sources", "OverviewInspectSources");
        inspectSources.Click += (_, _) => sources();
        _sourceHealth = new SettingsRow(Symbol.Message, "OverviewSourceAttention", inspectSources, "OverviewSourceHealth")
            { Visibility = Visibility.Collapsed, ShowSeparator = false };
        Children.Add(_sourceHealth);
        _preview = new IconActionButton(0xE7E7, "ActionPreview", "OverviewPreview");
        _preview.Click += (_, _) => preview();
        var updateAction = new IconActionButton(Symbol.Download, "ActionCheckUpdates", "OverviewUpdates");
        updateAction.Click += (_, _) => updates();
        var configure = new IconActionButton(Symbol.Permissions, "Setup", "OverviewConfigureNotifications");
        configure.Click += (_, _) => setup();
        Children.Add(new SettingsSection("OverviewQuickActions",
            new SettingsRow(Symbol.Message, "Sources", sourceAction),
            new SettingsRow(Symbol.View, "ActionPreview", _preview),
            new SettingsRow(Symbol.Download, "ActionCheckUpdates", updateAction),
            new SettingsRow(Symbol.Permissions, "Setup", configure)));
        SizeChanged += (_, _) =>
        {
            var narrow = ActualWidth < 400;
            Grid.SetColumn(_version, narrow ? 0 : 1);
            Grid.SetRow(_version, narrow ? 1 : 0);
            Grid.SetColumnSpan(_sources, narrow ? 2 : 1);
            Grid.SetColumnSpan(_version, narrow ? 2 : 1);
        };
    }

    public void Refresh(SettingsSnapshot snapshot, AppSettings settings, bool busy, UpdateCheckState? updates = null)
    {
        var enabled = snapshot.Sources.Count(source => source.Definition.IsEnabled(settings));
        var running = snapshot.Sources.Count(source => source.Definition.IsEnabled(settings) && SourceStatus.IsRunning(source.StatusKey));
        _sources.Value = $"{running} / {snapshot.Sources.Count}";
        _sources.Detail = Localization.Format("OverviewEnabledSources", enabled);
        var ready = !settings.SystemNotifications || snapshot.Initialized && snapshot.Access == NotificationAccess.Allowed;
        var disconnected = snapshot.Sources.Where(source => source.Definition.IsEnabled(settings)
            && !SourceStatus.IsRunning(source.StatusKey) && (ready || source.Definition.Id != BuiltInPluginIds.System)).ToArray();
        var failures = disconnected.Count(source => SourceStatus.Tone(source.StatusKey) == StatusTone.Critical);
        _sourceHealth.Visibility = disconnected.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _sourceHealth.Status = disconnected.Length == 0 ? "" : Localization.Format(
            failures > 0 ? "OverviewSourceFailures" : "OverviewSourceDisconnected", failures > 0 ? failures : disconnected.Length);
        _sourceHealth.Tone = failures > 0 ? StatusTone.Critical : StatusTone.Neutral;
        _version.Detail = updates switch
        {
            { Busy: true } => Localization.Get("UpdateChecking"),
            { ErrorKey: { } error } => Localization.Get(error),
            { Canceled: true } => Localization.Get("UpdateCheckCanceled"),
            { Candidate: { } candidate } => Localization.Format("OverviewUpdateAvailable", candidate.Tag),
            { Checked: true } => Localization.Get("UpdateNone"),
            not null => Localization.Get("UpdateNotChecked"),
            _ => ""
        };
        _notice.IsOpen = !ready;
        _notice.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        _notice.Title = ready ? "" : Localization.Get("OverviewSetupNeeded");
        _notice.Message = ready ? "" : Localization.Get(snapshot.Initialized ? "GrantAccessOrDisable" : "InitializeThenGrantAccess");
        _notice.Severity = InfoBarSeverity.Informational;
        _setup.IsEnabled = !busy;
        _preview.IsEnabled = !busy;
    }
}
