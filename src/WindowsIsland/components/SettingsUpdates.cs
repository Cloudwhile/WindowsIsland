using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class SettingsUpdates : StackPanel, IDisposable
{
    private readonly SettingsStore _store;
    private readonly Action _restart;
    private readonly UpdateCheckService _updates;
    private UpdateCheckState? _shownState;
    private bool _preserveResult;
    private readonly GitHubReleaseClient _client = new();
    private readonly ToggleSwitch _prereleases = new() { OnContent = null, OffContent = null, MinWidth = 0 };
    private readonly IconActionButton _check = new(Symbol.Refresh, "ActionCheckUpdates", "CheckForUpdates");
    private readonly IconActionButton _install = new(Symbol.Download, "ActionInstallUpdate", "InstallUpdate") { IsEnabled = false };
    private readonly IconActionButton _releasePage = new(Symbol.OpenFile, "ActionOpenRelease", "OpenReleasePage") { Visibility = Visibility.Collapsed };
    private readonly IconActionButton _cancel = new(Symbol.Cancel, "ActionCancel", "CancelUpdate") { Visibility = Visibility.Collapsed };
    private readonly InfoBar _status = new() { IsClosable = false, IsOpen = true, Visibility = Visibility.Collapsed };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
    private readonly TextBlock _releaseName = SettingsSection.Heading("", localizeTitle: false);
    private readonly MarkdownText _notes = new();
    private readonly SettingsContent _release = new() { Visibility = Visibility.Collapsed };
    private readonly SettingsRow _versionRow, _availableRow;
    private readonly TextBlock _size = IslandTheme.Text("", 12, secondary: true);
    private bool _checked;
    private long? _packageSize;
    private CancellationTokenSource? _operation;
    private GitHubRelease? _candidate;
    private bool _disposed, _syncing, _busy;
    private bool _includePrereleases;
    private string? _messageKey;
    private object[] _messageArguments = [];
    private double? _percent;
    private double? _bytesPerSecond;

    public SettingsUpdates(SettingsStore store, UpdateCheckService updates, Action restart)
    {
        _store = store;
        _updates = updates;
        _restart = restart;
        _versionRow = new SettingsRow(new FontIcon { Glyph = char.ConvertFromUtf32(0xE946), FontSize = 20 },
            "UpdateCurrentVersion", _check) { Status = ReleaseVersion.Current.Tag };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_releasePage);
        actions.Children.Add(_cancel);
        actions.Children.Add(_size);
        actions.Children.Add(_install);
        Children.Add(_versionRow);
        var channel = new SettingsRow(Symbol.Flag, "UpdateIncludePrereleases", _prereleases);
        Children.Add(channel);
        _availableRow = new SettingsRow(Symbol.Download, "UpdateAvailable", actions, "UpdateAvailableStatus");
        Children.Add(_availableRow);
        AutomationProperties.SetAutomationId(_size, "UpdatePackageSize");
        Children.Add(SettingsContent.Wrap(_status));
        Children.Add(SettingsContent.Wrap(_progress));
        _release.Children.Add(_releaseName);
        _release.Children.Add(_notes);
        Children.Add(_release);
        _check.Click += async (_, _) => await CheckAsync();
        _install.Click += async (_, _) => await InstallAsync();
        _cancel.Click += (_, _) => { if (_busy) _operation?.Cancel(); else _updates.Cancel(); };
        _releasePage.Click += async (_, _) =>
        {
            if (_candidate is not { } candidate) return;
            try { await Windows.System.Launcher.LaunchUriAsync(candidate.Page); }
            catch (Exception error) { Diagnostics.Capture(DiagnosticArea.Updates, error); Trace.WriteLine(error); if (!_disposed) Show("UpdateLinkError", InfoBarSeverity.Error); }
        };
        _prereleases.Toggled += (_, _) =>
        {
            if (_syncing || _disposed) return;
            try { _store.Save(_store.Current with { IncludePrereleaseUpdates = _prereleases.IsOn }); }
            catch (Exception error) { Diagnostics.Capture(DiagnosticArea.Updates, error); Trace.WriteLine(error); Show("UpdatePreferenceError", InfoBarSeverity.Error); Refresh(); }
        };
        LocalizedUI.Label(_prereleases, "UpdateIncludePrereleases");
        AutomationProperties.SetAutomationId(_prereleases, "PrereleaseUpdatesToggle");
        AutomationProperties.SetAutomationId(_status, "UpdateStatus");
        AutomationProperties.SetAutomationId(_progress, "UpdateProgress");
        AutomationProperties.SetAutomationId(_notes, "UpdateReleaseNotes");
        _store.Changed += OnSettingsChanged;
        LocalizedUI.Bind(this, RefreshLanguage);
        Refresh();
        _updates.Changed += OnCheckChanged;
        if (UpdateInstaller.ReadResult() is { } result)
        {
            _preserveResult = true;
            Show(result.Success ? "UpdateComplete" : result.MessageKey is "UpdateRestored" or "UpdateBackupRetained" ? result.MessageKey : "UpdateFailed",
                result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                result.Success ? [result.Version] : []);
        }
        _ = RefreshCheckAsync();
    }

    private async Task CheckAsync()
    {
        if (_busy || _disposed) return;
        _preserveResult = false;
        await _updates.CheckAsync();
    }

    private void OnCheckChanged() => DispatcherQueue.TryEnqueue(() => _ = RefreshCheckAsync());

    private async Task RefreshCheckAsync()
    {
        if (_disposed || _busy) return;
        var state = _updates.State;
        if (ReferenceEquals(state, _shownState)) return;
        _shownState = state;
        _candidate = state.Candidate;
        _checked = state.Checked;
        _packageSize = null;
        _release.Visibility = _releasePage.Visibility = _candidate is null ? Visibility.Collapsed : Visibility.Visible;
        _cancel.Visibility = _progress.Visibility = state.Busy ? Visibility.Visible : Visibility.Collapsed;
        _cancel.IsEnabled = true;
        _progress.IsIndeterminate = state.Busy;
        if (_candidate is { } candidate)
        {
            _releaseName.Text = candidate.Name;
            _notes.Text = string.IsNullOrWhiteSpace(candidate.Notes) ? Localization.Get("UpdateNotesEmpty") : candidate.Notes;
        }
        if (!_preserveResult)
        {
            if (state.Busy) Show("UpdateChecking", InfoBarSeverity.Informational);
            else if (state.ErrorKey is { } error) Show(error, InfoBarSeverity.Error);
            else if (state.Canceled) Show("UpdateCheckCanceled", InfoBarSeverity.Informational);
            else if (state.Candidate is { } found)
                Show(found.IsPrerelease ? "UpdateFoundPrerelease" : "UpdateFound", InfoBarSeverity.Informational, found.Tag);
            else if (state.Checked) Show("UpdateNone", InfoBarSeverity.Success);
            else { _messageKey = null; _status.Visibility = Visibility.Collapsed; }
        }
        Refresh();
        if (state.Candidate is not { } release) return;
        try
        {
            if (_disposed || !ReferenceEquals(state, _updates.State)) return;
            _packageSize = release.Installer is { } installer ? installer.Size : 0;
            RefreshAvailable();
        }
        catch (Exception error) { Diagnostics.Capture(DiagnosticArea.Updates, error); }
    }

    private async Task InstallAsync()
    {
        if (_busy || _disposed || _updates.State.Busy || _candidate is not { } candidate
            || !ReferenceEquals(candidate, _updates.State.Candidate)) return;
        Begin(TimeSpan.FromMinutes(15));
        Show("UpdatePreparing", InfoBarSeverity.Informational);
        try
        {
            var operation = _operation!;
            var progress = new Progress<UpdateProgress>(value =>
            {
                if (_disposed || !_busy || !ReferenceEquals(_operation, operation) || operation.IsCancellationRequested) return;
                Show(value.MessageKey, InfoBarSeverity.Informational);
                _percent = value.Percent;
                _bytesPerSecond = value.BytesPerSecond;
                RefreshLanguage();
                _progress.IsIndeterminate = !value.Percent.HasValue;
                _progress.Value = value.Percent ?? 0;
            });
            using var update = await UpdatePackage.PrepareAsync(_client, candidate, useInstaller: true,
                progress: progress, cancellationToken: _operation!.Token);
            if (_disposed) return;
            Show("UpdateInstalling", InfoBarSeverity.Informational);
            _cancel.IsEnabled = false;
            await UpdateInstaller.StartAsync(update, _operation.Token, restoreRegistration: AppInitialization.HasIdentity);
            _restart();
        }
        catch (OperationCanceledException) { if (!_disposed) Show("UpdateCanceled", InfoBarSeverity.Informational); }
        catch (Exception error) { Diagnostics.Capture(DiagnosticArea.Updates, error); Trace.WriteLine(error); if (!_disposed) Show(Localization.ErrorKey(error, "UpdateFailed"), InfoBarSeverity.Error); }
        finally { End(); }
    }

    private void Begin(TimeSpan timeout)
    {
        _busy = true;
        _operation = new CancellationTokenSource(timeout);
        _cancel.IsEnabled = true;
        _cancel.Visibility = _progress.Visibility = Visibility.Visible;
        _progress.IsIndeterminate = true;
        Refresh();
    }

    private void End()
    {
        _busy = false;
        _operation?.Dispose();
        _operation = null;
        if (_disposed) return;
        _progress.Visibility = _cancel.Visibility = Visibility.Collapsed;
        Refresh();
        if (!ReferenceEquals(_shownState, _updates.State)) _ = RefreshCheckAsync();
    }

    private void Show(string messageKey, InfoBarSeverity severity, params object[] arguments)
    {
        _messageKey = messageKey;
        _messageArguments = arguments;
        _percent = null;
        _bytesPerSecond = null;
        _status.Title = "";
        RefreshLanguage();
        _status.Severity = severity;
        _status.Visibility = Visibility.Visible;
    }

    private void RefreshLanguage()
    {
        if (_disposed) return;
        if (_messageKey is { } key)
        {
            var message = Localization.Format(key, _messageArguments);
            _status.Message = _percent is { } percent
                ? _bytesPerSecond is { } speed and > 0
                    ? Localization.Format("UpdateProgressPercentSpeed", message, percent, FormatSpeed(speed))
                    : Localization.Format("UpdateProgressPercent", message, percent)
                : message;
        }
        if (_candidate is { } candidate && string.IsNullOrWhiteSpace(candidate.Notes)) _notes.Text = Localization.Get("UpdateNotesEmpty");
        RefreshAvailable();
    }

    private void RefreshAvailable()
    {
        _availableRow.Status = _candidate is { } candidate ? candidate.Tag
            : _checked ? Localization.Get("UpdateUpToDate") : Localization.Get("UpdateNotChecked");
        _availableRow.Tone = _candidate is null ? StatusTone.Neutral : StatusTone.Success;
        _size.Text = _candidate is not null && _packageSize is { } size ? FormatSize(size) : "";
        _size.Visibility = _size.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    internal static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? Localization.Format("SizeMegabytes", Math.Round(bytes / 1024d / 1024d, 1))
        : Localization.Format("SizeKilobytes", Math.Max(1, (long)Math.Round(bytes / 1024d)));

    internal static string FormatSpeed(double bytesPerSecond)
    {
        var (value, unit) = bytesPerSecond switch
        {
            >= 1024d * 1024 * 1024 => (bytesPerSecond / 1024 / 1024 / 1024, "GB/s"),
            >= 1024d * 1024 => (bytesPerSecond / 1024 / 1024, "MB/s"),
            >= 1024d => (bytesPerSecond / 1024, "KB/s"),
            _ => (bytesPerSecond, "B/s")
        };
        return string.Format(Localization.Culture, "{0:0.0} {1}", value, unit);
    }

    private void OnSettingsChanged(AppSettings _) => Refresh();

    private void Refresh()
    {
        if (_disposed) return;
        var include = _store.Current.IncludePrereleaseUpdates;
        if (include != _includePrereleases)
        {
            _operation?.Cancel();
            _candidate = null;
            _checked = false;
            _preserveResult = false;
            _release.Visibility = _releasePage.Visibility = _status.Visibility = Visibility.Collapsed;
        }
        RefreshAvailable();
        _includePrereleases = include;
        _syncing = true;
        try { _prereleases.IsOn = include; }
        finally { _syncing = false; }
        _prereleases.IsEnabled = !_busy;
        _check.IsEnabled = !_busy && !_updates.State.Busy;
        _install.IsEnabled = !_busy && !_updates.State.Busy && _candidate is not null
            && ReferenceEquals(_candidate, _updates.State.Candidate);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Changed -= OnSettingsChanged;
        _updates.Changed -= OnCheckChanged;
        _operation?.Cancel();
        _client.Dispose();
    }
}
