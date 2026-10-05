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
    private readonly IconActionButton _check = new(Symbol.Refresh, "检查更新", "CheckForUpdates");
    private readonly IconActionButton _install = new(Symbol.Download, "下载更新并重启", "InstallUpdate") { IsEnabled = false };
    private readonly IconActionButton _releasePage = new(Symbol.OpenFile, "在 GitHub 查看此版本", "OpenReleasePage") { Visibility = Visibility.Collapsed };
    private readonly IconActionButton _cancel = new(Symbol.Cancel, "取消", "CancelUpdate") { Visibility = Visibility.Collapsed };
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

    public SettingsUpdates(SettingsStore store, Action restart)
    {
        _store = store;
        _restart = restart;
        _versionRow = new SettingsRow(new FontIcon { Glyph = char.ConvertFromUtf32(0xE946), FontSize = 20 },
            "当前版本", _check) { Status = "v" + ReleaseVersion.Current.Display };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_releasePage);
        actions.Children.Add(_cancel);
        actions.Children.Add(_install);
        Children.Add(_versionRow);
        var channel = new SettingsRow(Symbol.Flag, "接收预发布更新", _prereleases);
        Children.Add(channel);
        Children.Add(new SettingsRow(Symbol.Download, "可用更新", actions));
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
            catch (Exception error) { Trace.WriteLine(error); if (!_disposed) Show("链接暂时无法打开，请重试。", InfoBarSeverity.Error); }
        };
        _prereleases.Toggled += (_, _) =>
        {
            if (_syncing || _disposed) return;
            try { _store.Save(_store.Current with { IncludePrereleaseUpdates = _prereleases.IsOn }); }
            catch (Exception error) { Trace.WriteLine(error); Show("更新偏好未能保存，请重试。", InfoBarSeverity.Error); Refresh(); }
        };
        AutomationProperties.SetName(_prereleases, "接收预发布更新");
        AutomationProperties.SetAutomationId(_prereleases, "PrereleaseUpdatesToggle");
        AutomationProperties.SetAutomationId(_status, "UpdateStatus");
        AutomationProperties.SetAutomationId(_progress, "UpdateProgress");
        AutomationProperties.SetAutomationId(_notes, "UpdateReleaseNotes");
        ToolTipService.SetToolTip(_prereleases, "接收预发布更新");
        _store.Changed += OnSettingsChanged;
        Refresh();
        if (UpdateInstaller.ReadResult() is { } result)
            Show(result.Success ? "已更新至 v" + result.Version : result.Message,
                result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async Task CheckAsync()
    {
        if (_busy || _disposed) return;
        Begin(TimeSpan.FromSeconds(45));
        _candidate = null;
        _release.Visibility = _releasePage.Visibility = Visibility.Collapsed;
        Show("正在检查更新。", InfoBarSeverity.Informational);
        try
        {
            var candidate = await _client.FindUpdateAsync(ReleaseVersion.Current, _includePrereleases, _operation!.Token);
            if (_disposed) return;
            _candidate = candidate;
            if (candidate is null) Show("没有可用的新版本。", InfoBarSeverity.Success);
            else
            {
                Show("发现新版本 v" + candidate.Version.Display + (candidate.IsPrerelease ? "（预发布）" : "") + "，更新后将重启消息岛。", InfoBarSeverity.Informational);
                _releaseName.Text = candidate.Name;
                _notes.Text = candidate.Notes.Length == 0 ? "此版本未提供更新说明。" : candidate.Notes;
                _release.Visibility = _releasePage.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException) { if (!_disposed) Show("检查已取消或超时，可重试。", InfoBarSeverity.Informational); }
        catch (Exception error) { Trace.WriteLine(error); if (!_disposed) Show(ErrorMessage(error, "无法获取更新，请检查网络后重试。"), InfoBarSeverity.Error); }
        finally { End(); }
    }

    private async Task InstallAsync()
    {
        if (_busy || _disposed || _candidate is not { } candidate) return;
        Begin(TimeSpan.FromMinutes(15));
        Show("正在准备更新。", InfoBarSeverity.Informational);
        try
        {
            var operation = _operation!;
            var progress = new Progress<UpdateProgress>(value =>
            {
                if (_disposed || !_busy || !ReferenceEquals(_operation, operation) || operation.IsCancellationRequested) return;
                Show(value.Message + (value.Percent is { } percent ? $" {percent:0}%" : ""), InfoBarSeverity.Informational);
                _progress.IsIndeterminate = !value.Percent.HasValue;
                _progress.Value = value.Percent ?? 0;
            });
            using var update = await UpdatePackage.PrepareAsync(_client, candidate, UpdateInstaller.IsMsiInstallation(), progress, _operation!.Token);
            if (_disposed) return;
            Show("正在安装更新并重启消息岛。", InfoBarSeverity.Informational);
            _cancel.IsEnabled = false;
            await UpdateInstaller.StartAsync(update, _operation.Token);
            _restart();
        }
        catch (OperationCanceledException) { if (!_disposed) Show("更新已取消或超时，原版本可以继续使用。", InfoBarSeverity.Informational); }
        catch (Exception error) { Trace.WriteLine(error); if (!_disposed) Show(ErrorMessage(error, "更新未完成，请重试。"), InfoBarSeverity.Error); }
        finally { End(); }
    }

    private static string ErrorMessage(Exception error, string fallback) => error switch
    {
        InvalidDataException => error.Message,
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests } => error.Message,
        _ => fallback
    };

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

    private void Show(string message, InfoBarSeverity severity)
    {
        _status.Title = "";
        _status.Message = message;
        _status.Severity = severity;
        _status.Visibility = Visibility.Visible;
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
