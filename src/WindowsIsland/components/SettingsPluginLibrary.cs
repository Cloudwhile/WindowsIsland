using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class SettingsPluginLibrary : StackPanel, IDisposable
{
    private readonly PluginLibrary _library;
    private readonly Func<CancellationToken, Task<string?>> _pick;
    private readonly IconActionButton _install = new(Symbol.Add, "PluginInstall", "InstallPlugin");
    private readonly IconActionButton _cancel = new(Symbol.Cancel, "PluginCancel", "CancelPluginInstallation") { Visibility = Visibility.Collapsed };
    private readonly InfoBar _notice = new() { IsOpen = true, IsClosable = false, Visibility = Visibility.Collapsed };
    private readonly SettingsOfficialPlugins _official;
    private readonly SettingsInstalledPlugins _installed;
    private readonly SelectorBar _views = new();
    private readonly SelectorBarItem _installedTab = new();
    private readonly SelectorBarItem _discoverTab = new();
    private readonly ProgressRing _progress = new() { Width = 20, Height = 20, IsActive = false, Visibility = Visibility.Collapsed };
    private CancellationTokenSource? _operation;
    private ContentDialog? _dialog;
    private string? _noticeKey;
    private bool _disposed;

    public SettingsPluginLibrary(PluginLibrary library, Func<CancellationToken, Task<string?>> pick, Action<SettingsDetail> open,
        Action? openDiagnostics = null, SettingsStore? settings = null)
    {
        _library = library;
        _pick = pick;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = SettingsTokens.ControlSpacing };
        actions.Children.Add(_progress);
        actions.Children.Add(_install);
        actions.Children.Add(_cancel);
        var toolbar = new Grid { ColumnSpacing = SettingsTokens.ControlSpacing };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _views.Items.Add(_installedTab);
        _views.Items.Add(_discoverTab);
        _views.SelectedItem = _installedTab;
        AutomationProperties.SetAutomationId(_views, "PluginLibraryViews");
        AutomationProperties.SetAutomationId(_installedTab, "PluginInstalledView");
        AutomationProperties.SetAutomationId(_discoverTab, "PluginDiscoverView");
        toolbar.Children.Add(_views);
        Grid.SetColumn(actions, 1);
        toolbar.Children.Add(actions);
        Children.Add(toolbar);
        Children.Add(SettingsContent.Wrap(_notice));
        _installed = new SettingsInstalledPlugins(library, open, openDiagnostics, settings);
        Children.Add(_installed);
        _official = new SettingsOfficialPlugins(library, open);
        Children.Add(_official);
        _views.SelectionChanged += (_, _) => UpdateView();
        _install.Click += async (_, _) => await ImportAsync();
        _cancel.Click += (_, _) => _operation?.Cancel();
        LocalizedUI.Bind(this, () =>
        {
            _installedTab.Text = Localization.Get("PluginInstalledView");
            _discoverTab.Text = Localization.Get("PluginDiscoverView");
            AutomationProperties.SetName(_installedTab, _installedTab.Text);
            AutomationProperties.SetName(_discoverTab, _discoverTab.Text);
            if (_noticeKey is { } key) _notice.Message = Localization.Get(key);
        });
        UpdateView();
    }

    private void UpdateView()
    {
        var installed = ReferenceEquals(_views.SelectedItem, _installedTab);
        _installed.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        _official.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        Refresh();
    }

    private async Task ImportAsync()
    {
        if (_operation is not null || _disposed) return;
        using var operation = _operation = new CancellationTokenSource();
        _install.IsEnabled = false;
        _progress.IsActive = true;
        _progress.Visibility = Visibility.Visible;
        _cancel.Visibility = Visibility.Visible;
        _notice.Visibility = Visibility.Collapsed;
        try
        {
            await Task.Yield();
            var file = await _pick(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (file is null) return;
            using var prepared = await _library.Installer.PrepareAsync(file, token: operation.Token);
            try { await _library.Manager.InstallAsync(prepared, token: operation.Token); }
            catch (PluginAlreadyInstalledException)
            {
                if (!await ConfirmOverwriteAsync()) return;
                await _library.Manager.InstallAsync(prepared, overwrite: true, token: operation.Token);
            }
            Show("PluginInstalledSuccess", InfoBarSeverity.Success);
            if (_disposed) return;
            _views.SelectedItem = _installedTab;
            _installed.Refresh();
        }
        catch (OperationCanceledException) { Show("PluginInstallCanceled", InfoBarSeverity.Informational); }
        catch (Exception error)
        {
            Diagnostics.Capture(DiagnosticArea.Application, error, "InstallPlugin");
            Show(PluginErrors.Key(error), InfoBarSeverity.Error);
        }
        finally
        {
            _operation = null;
            if (!_disposed)
            {
                _install.IsEnabled = true;
                _cancel.Visibility = _progress.Visibility = Visibility.Collapsed;
                _progress.IsActive = false;
            }
        }
    }

    private async Task<bool> ConfirmOverwriteAsync()
    {
        if (_disposed) return false;
        // 对话框需要明确的确认文案，普通工具栏操作继续使用图标。
        _dialog = new ContentDialog { XamlRoot = XamlRoot, Title = Localization.Get("PluginAlreadyInstalled"),
            Content = Localization.Get("PluginOverwriteQuestion"), PrimaryButtonText = Localization.Get("PluginOverwrite"),
            CloseButtonText = Localization.Get("PluginCancel"), DefaultButton = ContentDialogButton.Close };
        try { return await _dialog.ShowAsync() == ContentDialogResult.Primary; }
        finally { _dialog = null; }
    }

    private void Show(string key, InfoBarSeverity severity)
    {
        if (_disposed) return;
        _notice.Severity = severity;
        _noticeKey = key;
        _notice.Message = Localization.Get(key);
        _notice.Visibility = Visibility.Visible;
    }

    public void Refresh()
    {
        if (_disposed) return;
        _installed.Refresh();
        _official.Refresh();
    }
    public void Dispose() { _disposed = true; _dialog?.Hide(); _operation?.Cancel(); _installed.Dispose(); _official.Dispose(); }
}
