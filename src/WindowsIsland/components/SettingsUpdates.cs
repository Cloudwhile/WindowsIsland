using System.Diagnostics;
using System.Net.Http;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class SettingsUpdates : StackPanel, IDisposable
{
    private readonly SettingsStore _store;
    private readonly Action _restart;
    private readonly GitHubReleaseClient _client = new();
    private readonly ToggleSwitch _prereleases = new() { OnContent = null, OffContent = null, MinWidth = 0 };
    private readonly IconActionButton _check = new(Symbol.Refresh, "ActionCheckUpdates", "CheckForUpdates");
    private readonly IconActionButton _install = new(Symbol.Download, "ActionInstallUpdate", "InstallUpdate") { IsEnabled = false };
    private readonly IconActionButton _releasePage = new(Symbol.OpenFile, "ActionOpenRelease", "OpenReleasePage") { Visibility = Visibility.Collapsed };
    private readonly IconActionButton _cancel = new(Symbol.Cancel, "ActionCancel", "CancelUpdate") { Visibility = Visibility.Collapsed };
    private readonly InfoBar _status = new() { IsClosable = false, IsOpen = true, Visibility = Visibility.Collapsed };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed, Margin = new Thickness(12, 12, 12, 4) };
    private readonly TextBlock _releaseName = IslandTheme.Text("", 18);
    private readonly TextBlock _notes = IslandTheme.Text("");
    private readonly StackPanel _release = new() { Spacing = 12, Margin = new Thickness(12, 20, 12, 0), Visibility = Visibility.Collapsed };
    private readonly SettingsRow _versionRow;
    private CancellationTokenSource? _operation;
    private GitHubRelease? _candidate;
    private bool _disposed, _syncing, _busy;
    private bool _includePrereleases;
    private string? _messageKey;
    private object[] _messageArguments = [];
    private double? _percent;

    public SettingsUpdates(SettingsStore store, Action restart)
    {
        _store = store;
        _restart = restart;
        _versionRow = new SettingsRow(new FontIcon { Glyph = char.ConvertFromUtf32(0xE946), FontSize = 20 },
            "UpdateCurrentVersion", _check) { Status = "v" + ReleaseVersion.Current.Display };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_releasePage);
        actions.Children.Add(_cancel);
        actions.Children.Add(_install);
        Children.Add(_versionRow);
        var channel = new SettingsRow(Symbol.Flag, "UpdateIncludePrereleases", _prereleases);
        Children.Add(channel);
        Children.Add(new SettingsRow(Symbol.Download, "UpdateAvailable", actions));
        _status.Margin = new Thickness(0, 12, 0, 0);
        Children.Add(_status);
        Children.Add(_progress);
        _releaseName.FontWeight = FontWeights.SemiBold;
        _releaseName.TextWrapping = TextWrapping.Wrap;
        _notes.TextWrapping = TextWrapping.Wrap;
        _notes.TextTrimming = TextTrimming.None;
        _notes.IsTextSelectionEnabled = true;
        _release.Children.Add(_releaseName);
        _release.Children.Add(_notes);
        Children.Add(_release);
        _check.Click += async (_, _) => await CheckAsync();
        _install.Click += async (_, _) => await InstallAsync();
        _cancel.Click += (_, _) => _operation?.Cancel();
        _releasePage.Click += async (_, _) =>
        {
            if (_candidate is not { } candidate) return;
            try { await Windows.System.Launcher.LaunchUriAsync(candidate.Page); }
            catch (Exception error) { Trace.WriteLine(error); if (!_disposed) Show("UpdateLinkError", InfoBarSeverity.Error); }
        };
        _prereleases.Toggled += (_, _) =>
        {
            if (_syncing || _disposed) return;
            try { _store.Save(_store.Current with { IncludePrereleaseUpdates = _prereleases.IsOn }); }
            catch (Exception error) { Trace.WriteLine(error); Show("UpdatePreferenceError", InfoBarSeverity.Error); Refresh(); }
        };
        LocalizedUI.Label(_prereleases, "UpdateIncludePrereleases");
        AutomationProperties.SetAutomationId(_prereleases, "PrereleaseUpdatesToggle");
        AutomationProperties.SetAutomationId(_status, "UpdateStatus");
        AutomationProperties.SetAutomationId(_progress, "UpdateProgress");
        AutomationProperties.SetAutomationId(_notes, "UpdateReleaseNotes");
        _store.Changed += OnSettingsChanged;
        LocalizedUI.Bind(this, RefreshLanguage);
        Refresh();
        if (UpdateInstaller.ReadResult() is { } result)
            Show(result.Success ? "UpdateComplete" : result.MessageKey is "UpdateRestored" or "UpdateBackupRetained" ? result.MessageKey : "UpdateFailed",
                result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                result.Success ? [result.Version] : []);
    }

    private async Task CheckAsync()
    {
        if (_busy || _disposed) return;
        Begin(TimeSpan.FromSeconds(45));
        _candidate = null;
        _release.Visibility = _releasePage.Visibility = Visibility.Collapsed;
        Show("UpdateChecking", InfoBarSeverity.Informational);
        try
        {
            var candidate = await _client.FindUpdateAsync(ReleaseVersion.Current, _includePrereleases, _operation!.Token);
            if (_disposed) return;
            _candidate = candidate;
            if (candidate is null) Show("UpdateNone", InfoBarSeverity.Success);
            else
            {
                Show(candidate.IsPrerelease ? "UpdateFoundPrerelease" : "UpdateFound", InfoBarSeverity.Informational, candidate.Version.Display);
                _releaseName.Text = candidate.Name;
                _notes.Text = candidate.Notes.Length == 0 ? Localization.Get("UpdateNotesEmpty") : candidate.Notes;
                _release.Visibility = _releasePage.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException) { if (!_disposed) Show("UpdateCheckCanceled", InfoBarSeverity.Informational); }
        catch (Exception error) { Trace.WriteLine(error); if (!_disposed) Show(Localization.ErrorKey(error, "UpdateFetchError"), InfoBarSeverity.Error); }
        finally { End(); }
    }

    private async Task InstallAsync()
    {
        if (_busy || _disposed || _candidate is not { } candidate) return;
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
                RefreshLanguage();
                _progress.IsIndeterminate = !value.Percent.HasValue;
                _progress.Value = value.Percent ?? 0;
            });
            using var update = await UpdatePackage.PrepareAsync(_client, candidate, UpdateInstaller.IsMsiInstallation(), progress, _operation!.Token);
            if (_disposed) return;
            Show("UpdateInstalling", InfoBarSeverity.Informational);
            _cancel.IsEnabled = false;
            await UpdateInstaller.StartAsync(update, _operation.Token);
            _restart();
        }
        catch (OperationCanceledException) { if (!_disposed) Show("UpdateCanceled", InfoBarSeverity.Informational); }
        catch (Exception error) { Trace.WriteLine(error); if (!_disposed) Show(Localization.ErrorKey(error, "UpdateFailed"), InfoBarSeverity.Error); }
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
    }

    private void Show(string messageKey, InfoBarSeverity severity, params object[] arguments)
    {
        _messageKey = messageKey;
        _messageArguments = arguments;
        _percent = null;
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
            _status.Message = _percent is { } percent ? Localization.Format("UpdateProgressPercent", message, percent) : message;
        }
        if (_candidate is { Notes.Length: 0 }) _notes.Text = Localization.Get("UpdateNotesEmpty");
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
            _release.Visibility = _releasePage.Visibility = _status.Visibility = Visibility.Collapsed;
        }
        _includePrereleases = include;
        _syncing = true;
        try { _prereleases.IsOn = include; }
        finally { _syncing = false; }
        _prereleases.IsEnabled = _check.IsEnabled = !_busy;
        _install.IsEnabled = !_busy && _candidate is not null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Changed -= OnSettingsChanged;
        _operation?.Cancel();
        _client.Dispose();
    }
}
