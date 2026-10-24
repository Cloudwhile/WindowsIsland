using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class SettingsDiagnostics : StackPanel, IDisposable
{
    private readonly SettingsStore _settings;
    private readonly Func<Task<string?>> _pickFile;
    private readonly ToggleSwitch _record = new() { OnContent = null, OffContent = null, MinWidth = 0 };
    private readonly IconActionButton _generate = new(Symbol.Refresh, "DiagnosticsGenerate", "GenerateDiagnostics");
    private readonly IconActionButton _view = new(Symbol.View, "DiagnosticsView", "ViewDiagnostics");
    private readonly IconActionButton _export = new(Symbol.Save, "DiagnosticsExport", "ExportDiagnostics");
    private readonly IconActionButton _clear = new(Symbol.Delete, "DiagnosticsClear", "ClearDiagnostics");
    private readonly IconActionButton _copy = new(Symbol.Copy, "DiagnosticsCopy", "CopyDiagnostics");
    private readonly IconActionButton _feedback = new(Symbol.Send, "DiagnosticsFeedback", "OpenFeedback");
    private readonly TextBox _details = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        MaxHeight = 360, Visibility = Visibility.Collapsed
    };
    private readonly InfoBar _status = new() { IsClosable = false, IsOpen = false, Visibility = Visibility.Collapsed };
    private readonly SettingsRow _recordRow, _recentRow, _feedbackRow;
    private readonly SettingsDiagnosticEntries _entries = new();
    private readonly SettingsEmptyState _empty = new(Symbol.Document, "DiagnosticsEmpty");
    private readonly ProgressRing _working = new() { Width = 20, Height = 20, Visibility = Visibility.Collapsed };
    private ContentDialog? _dialog;
    private bool _syncing, _busy, _disposed;
    private string? _statusKey;

    public SettingsDiagnostics(SettingsStore settings, Func<Task<string?>> pickFile)
    {
        _settings = settings;
        _pickFile = pickFile;
        _recordRow = new SettingsRow(Symbol.Document, "DiagnosticsRecord", _record) { ShowSeparator = false };
        _recentRow = new SettingsRow(Symbol.Important, "DiagnosticsRecords", new Grid(), "DiagnosticsSummary") { ShowSeparator = false };
        var privacy = LocalizedUI.Text("DiagnosticsPrivacy", 12, secondary: true);
        privacy.TextWrapping = TextWrapping.Wrap;
        Children.Add(new SettingsSection("DiagnosticsOverview", _recordRow, new SettingsContent(privacy), _recentRow));
        var actions = new SettingsContent(new SettingsMaintenanceActions(_generate, _view, _copy, _export, _clear, _working));
        Children.Add(new SettingsSection("DiagnosticsActions", actions, SettingsContent.Wrap(_details), SettingsContent.Wrap(_status)));
        Children.Add(new SettingsSection("DiagnosticsEvents", _entries, SettingsContent.Wrap(_empty)));
        _feedbackRow = new SettingsRow(Symbol.Send, "DiagnosticsFeedback", _feedback) { ShowSeparator = false };
        Children.Add(new SettingsSection("DiagnosticsFeedbackTitle", _feedbackRow));
        AutomationProperties.SetAutomationId(_record, "LocalDiagnosticsToggle");
        AutomationProperties.SetAutomationId(_details, "DiagnosticDetails");
        AutomationProperties.SetAutomationId(_status, "DiagnosticStatus");
        LocalizedUI.Label(_record, "DiagnosticsRecord");
        LocalizedUI.Label(_details, "DiagnosticsView");
        _record.Toggled += (_, _) =>
        {
            if (_syncing || _disposed) return;
            try
            {
                _settings.Save(_settings.Current with { LocalDiagnostics = _record.IsOn });
                Diagnostics.Store?.SetEnabled(_record.IsOn);
            }
            catch (Exception error) { Diagnostics.Capture(DiagnosticArea.Settings, error); Show("ErrorSettingsSave", true); }
            Refresh();
        };
        _generate.Click += async (_, _) => await RunAsync(async () =>
        {
            var report = await ReportAsync();
            if (_disposed) return;
            if (_details.Visibility == Visibility.Visible) _details.Text = report;
            Show("DiagnosticsGenerated");
        });
        _view.Click += async (_, _) => await RunAsync(async () =>
        {
            if (_details.Visibility == Visibility.Visible) { _details.Visibility = Visibility.Collapsed; return; }
            var report = await ReportAsync();
            if (_disposed) return;
            _details.Text = report;
            _details.Visibility = Visibility.Visible;
        });
        _export.Click += async (_, _) => await RunAsync(async () =>
        {
            var path = await _pickFile();
            if (_disposed) return;
            if (path is null) { Show("DiagnosticsExportCanceled", severity: InfoBarSeverity.Informational); return; }
            var store = Diagnostics.Store ?? throw new InvalidOperationException("Diagnostics unavailable.");
            await store.ExportAsync(path);
            if (!_disposed) Show("DiagnosticsExported");
        });
        _copy.Click += async (_, _) => await RunAsync(async () =>
        {
            var report = await ReportAsync();
            if (_disposed) return;
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(report);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Show("DiagnosticsCopied");
        });
        _clear.Click += async (_, _) => await RunAsync(async () =>
        {
            if (!await ConfirmClearAsync() || _disposed) return;
            await Task.Run(() => Diagnostics.Store?.Clear());
            if (_disposed) return;
            _details.Text = "";
            _details.Visibility = Visibility.Collapsed;
            Show("DiagnosticsCleared");
        });
        _feedback.Click += async (_, _) => await RunAsync(async () =>
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(DiagnosticReports.FeedbackUri()))
                throw new InvalidOperationException("Feedback page did not open.");
            if (!_disposed) Show("DiagnosticsFeedbackOpened");
        });
        _settings.Changed += SettingsChanged;
        LocalizedUI.Bind(this, Refresh);
        Refresh();
    }

    public void Refresh()
    {
        if (_disposed) return;
        _syncing = true;
        _record.IsOn = _settings.Current.LocalDiagnostics;
        _syncing = false;
        var store = Diagnostics.Store;
        var unavailable = store is null || store.PersistenceFailed;
        _recordRow.Status = Localization.Get(unavailable ? "DiagnosticsUnavailable" : _record.IsOn ? "DiagnosticsLocalOnly" : "DiagnosticsStopped");
        _recordRow.Tone = unavailable ? StatusTone.Critical : StatusTone.Neutral;
        var entries = store?.Snapshot() ?? [];
        var failures = entries.Count(entry => DiagnosticPresentation.Outcome(entry) == DiagnosticOutcome.Failure);
        _recentRow.Status = entries.Length == 0 ? Localization.Get("DiagnosticsEmpty") : Localization.Format("DiagnosticsRecordCount", entries.Length, failures);
        _recentRow.Tone = failures > 0 ? StatusTone.Critical : StatusTone.Neutral;
        _recentRow.Detail = entries.Where(entry => DiagnosticPresentation.Outcome(entry) == DiagnosticOutcome.Failure)
            .Select(entry => (DateTimeOffset?)entry.LastSeen).Max() is { } latest
            ? Localization.Format("DiagnosticsLatestFailure", latest.ToLocalTime().ToString("g", Localization.Culture))
            : Localization.Get("DiagnosticsNoFailures");
        _entries.Refresh(entries.Reverse().ToArray());
        _empty.Visibility = entries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _record.IsEnabled = !_busy;
        _generate.IsEnabled = _view.IsEnabled = _export.IsEnabled = _copy.IsEnabled = !_busy && store is not null;
        _clear.IsEnabled = !_busy && entries.Length > 0;
        _feedback.IsEnabled = !_busy;
        _working.IsActive = _busy;
        _working.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        if (_statusKey is not null) _status.Message = Localization.Get(_statusKey);
        _feedbackRow.Status = Localization.Get("DiagnosticsFeedbackHint");
        if (_dialog is { } dialog)
        {
            dialog.Title = Localization.Get("DiagnosticsClearConfirmTitle");
            dialog.Content = Localization.Get("DiagnosticsClearConfirmMessage");
            dialog.PrimaryButtonText = Localization.Get("DiagnosticsConfirmClear");
            dialog.CloseButtonText = Localization.Get("ActionCancel");
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _disposed) return;
        _busy = true;
        _statusKey = null;
        _status.Visibility = Visibility.Collapsed;
        Refresh();
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Diagnostics.Capture(DiagnosticArea.Diagnostics, error);
            if (!_disposed) Show("DiagnosticsActionFailed", true);
        }
        finally { _busy = false; Refresh(); }
    }

    private static Task<string> ReportAsync() => Diagnostics.Store?.Reports.GetAsync()
        ?? throw new InvalidOperationException("Diagnostics unavailable.");

    private async Task<bool> ConfirmClearAsync()
    {
        if (_disposed || XamlRoot is null) return false;
        _dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = Localization.Get("DiagnosticsClearConfirmTitle"),
            Content = Localization.Get("DiagnosticsClearConfirmMessage"),
            PrimaryButtonText = Localization.Get("DiagnosticsConfirmClear"), CloseButtonText = Localization.Get("ActionCancel"),
            DefaultButton = ContentDialogButton.Close
        };
        AutomationProperties.SetAutomationId(_dialog, "ClearDiagnosticsConfirmation");
        try { return await _dialog.ShowAsync() == ContentDialogResult.Primary; }
        finally { _dialog = null; }
    }

    private void Show(string key, bool error = false, InfoBarSeverity severity = InfoBarSeverity.Success)
    {
        _statusKey = key;
        _status.IsOpen = true;
        _status.Visibility = Visibility.Visible;
        _status.Message = Localization.Get(key);
        _status.Severity = error ? InfoBarSeverity.Error : severity;
    }
    private void SettingsChanged(AppSettings _) => Refresh();
    public void Dispose() { _disposed = true; _dialog?.Hide(); _settings.Changed -= SettingsChanged; }
}
