using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class SettingsDiagnostics : StackPanel, IDisposable
{
    private const int VisibleEntries = 20;
    private readonly SettingsStore _settings;
    private readonly Func<Task<string?>> _pickFile;
    private readonly Func<Task> _retry;
    private readonly ToggleSwitch _record = new() { OnContent = null, OffContent = null, MinWidth = 0 };
    private readonly IconActionButton _view = new(Symbol.View, "DiagnosticsView", "ViewDiagnostics");
    private readonly IconActionButton _export = new(Symbol.Save, "DiagnosticsExport", "ExportDiagnostics");
    private readonly IconActionButton _clear = new(Symbol.Delete, "DiagnosticsClear", "ClearDiagnostics");
    private readonly IconActionButton _copy = new(Symbol.Copy, "DiagnosticsCopy", "CopyDiagnostics");
    private readonly IconActionButton _feedback = new(Symbol.Send, "DiagnosticsFeedback", "OpenFeedback");
    private readonly TextBox _details = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        MaxHeight = 360, Margin = new Thickness(12), Visibility = Visibility.Collapsed
    };
    private readonly StackPanel _entries = new();
    private readonly HashSet<string> _expanded = [];
    private readonly InfoBar _status = new() { IsClosable = false, IsOpen = false, Margin = new Thickness(0, 12, 0, 0) };
    private readonly SettingsRow _recordRow, _recentRow, _feedbackRow;
    private bool _syncing, _busy, _disposed;
    private string? _statusKey, _signature;

    public SettingsDiagnostics(SettingsStore settings, Func<Task<string?>> pickFile, Func<Task> retry)
    {
        _settings = settings;
        _pickFile = pickFile;
        _retry = retry;
        _recordRow = new SettingsRow(Symbol.Document, "DiagnosticsRecord", _record);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_view); actions.Children.Add(_export); actions.Children.Add(_clear);
        _recentRow = new SettingsRow(Symbol.Important, "DiagnosticsRecent", actions, "DiagnosticsSummary");
        Children.Add(_recordRow);
        Children.Add(_recentRow);
        Children.Add(_entries);
        Children.Add(_details);
        var feedback = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        feedback.Children.Add(_copy); feedback.Children.Add(_feedback);
        _feedbackRow = new SettingsRow(Symbol.Send, "DiagnosticsFeedbackTitle", feedback);
        Children.Add(_feedbackRow);
        var privacy = LocalizedUI.Text("DiagnosticsPrivacy", 12, secondary: true);
        privacy.TextWrapping = TextWrapping.Wrap;
        privacy.Margin = new Thickness(12, 12, 12, 4);
        Children.Add(privacy);
        Children.Add(_status);
        AutomationProperties.SetAutomationId(_record, "LocalDiagnosticsToggle");
        AutomationProperties.SetAutomationId(_details, "DiagnosticDetails");
        AutomationProperties.SetAutomationId(_entries, "DiagnosticEntries");
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
        _view.Click += async (_, _) => await RunAsync(async () =>
        {
            if (_details.Visibility == Visibility.Visible) { _details.Visibility = Visibility.Collapsed; return; }
            var report = await Task.Run(() => Diagnostics.Store?.ReportJson() ?? "{}");
            if (_disposed) return;
            _details.Text = report;
            _details.Visibility = Visibility.Visible;
        });
        _export.Click += async (_, _) => await RunAsync(async () =>
        {
            var path = await _pickFile();
            if (path is null || _disposed) return;
            var store = Diagnostics.Store ?? throw new InvalidOperationException("Diagnostics unavailable.");
            await store.ExportAsync(path);
            if (!_disposed) Show("DiagnosticsExported");
        });
        _copy.Click += async (_, _) => await RunAsync(async () =>
        {
            var report = await Task.Run(() => Diagnostics.Store?.ReportJson()) ?? throw new InvalidOperationException("Diagnostics unavailable.");
            if (_disposed) return;
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(report);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Show("DiagnosticsCopied");
        });
        _clear.Click += async (_, _) => await RunAsync(async () =>
        {
            await Task.Run(() => Diagnostics.Store?.Clear());
            if (_disposed) return;
            _details.Text = "";
            _details.Visibility = Visibility.Collapsed;
            _expanded.Clear();
            Show("DiagnosticsCleared");
        });
        _feedback.Click += async (_, _) => await RunAsync(async () =>
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(DiagnosticReports.FeedbackUri()))
                throw new InvalidOperationException("Feedback page did not open.");
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
        RefreshEntries(entries);
        _record.IsEnabled = !_busy;
        _view.IsEnabled = _export.IsEnabled = _copy.IsEnabled = !_busy && store is not null;
        _clear.IsEnabled = !_busy && entries.Length > 0;
        _feedback.IsEnabled = !_busy;
        if (_statusKey is not null) _status.Message = Localization.Get(_statusKey);
        _feedbackRow.Status = Localization.Get("DiagnosticsFeedbackHint");
    }

    // Rebuilt only when records or the language change, so the 1 s refresh keeps expansion and focus.
    private void RefreshEntries(DiagnosticEntry[] entries)
    {
        var latest = Enumerable.Reverse(entries).Take(VisibleEntries).ToArray();
        var signature = Localization.Language + "|" + string.Join(';', latest.Select(entry => entry.Id + ":" + entry.Count + ":" + entry.LastSeen.Ticks));
        if (signature == _signature) return;
        _signature = signature;
        _expanded.IntersectWith(latest.Select(entry => entry.Id));
        _entries.Children.Clear();
        foreach (var entry in latest) _entries.Children.Add(Entry(entry));
    }

    private SettingsExpander Entry(DiagnosticEntry entry)
    {
        var success = DiagnosticPresentation.Outcome(entry) == DiagnosticOutcome.Success;
        var icon = new FontIcon { Glyph = char.ConvertFromUtf32(success ? 0xE73E : 0xE783), FontSize = 16 };
        var row = new SettingsExpander(icon, DiagnosticPresentation.Title(entry), "DiagnosticEntry." + entry.Id,
            "DiagnosticEntryStatus." + entry.Id, localizeTitle: false);
        row.Header.Status = DiagnosticPresentation.Summary(entry);
        row.Header.Tone = success ? StatusTone.Success : StatusTone.Critical;
        var lines = new StackPanel { Spacing = 4, Padding = new Thickness(12, 4, 12, 10) };
        void Line(string text)
        {
            var block = IslandTheme.Text(text, 12, secondary: true);
            block.TextWrapping = TextWrapping.Wrap;
            block.IsTextSelectionEnabled = true;
            lines.Children.Add(block);
        }
        Line(Localization.Format("DiagnosticTime", Time(entry.FirstSeen), Time(entry.LastSeen)));
        Line(entry.ClientVersion is { } client
            ? Localization.Format("DiagnosticVersionsWithClient", entry.AppVersion, Localization.Get("DiagnosticsArea" + entry.Area), client)
            : Localization.Format("DiagnosticVersion", entry.AppVersion));
        if (DiagnosticPresentation.Reason(entry) is { } reason) Line(Localization.Format("DiagnosticReason", reason));
        if (DiagnosticPresentation.Suggestion(entry) is { } suggestion) Line(Localization.Format("DiagnosticSuggestion", suggestion));
        Line(Localization.Format("DiagnosticShortId", DiagnosticPresentation.ShortId(entry)));
        if (DiagnosticPresentation.CanRetrySource(entry))
        {
            var retry = new IconActionButton(Symbol.Refresh, "ActionRetrySource", "DiagnosticRetry." + entry.Id)
            { HorizontalAlignment = HorizontalAlignment.Left };
            retry.Click += async (_, _) => await RunAsync(async () => { await _retry(); if (!_disposed) Show("DiagnosticsRetryStarted"); });
            lines.Children.Add(retry);
        }
        row.Rows.Add(lines);
        row.IsExpanded = _expanded.Contains(entry.Id);
        row.ExpandedChanged += expanded => { if (expanded) _expanded.Add(entry.Id); else _expanded.Remove(entry.Id); };
        return row;
    }

    private static string Time(DateTimeOffset value) => value.ToLocalTime().ToString("g", Localization.Culture);

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _disposed) return;
        _busy = true;
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

    private void Show(string key, bool error = false)
    {
        _statusKey = key;
        _status.IsOpen = true;
        _status.Message = Localization.Get(key);
        _status.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Success;
    }
    private void SettingsChanged(AppSettings _) => Refresh();
    public void Dispose() { _disposed = true; _settings.Changed -= SettingsChanged; }
}
