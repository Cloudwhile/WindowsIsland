using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class SettingsPluginLibrary : StackPanel, IDisposable
{
    private readonly PluginLibrary _library;
    private readonly Func<Task<string?>> _pick;
    private readonly IconActionButton _install = new(Symbol.Add, "PluginInstall", "InstallPlugin");
    private readonly IconActionButton _cancel = new(Symbol.Cancel, "PluginCancel", "CancelPluginInstallation") { Visibility = Visibility.Collapsed };
    private readonly InfoBar _notice = new() { IsOpen = true, IsClosable = false, Visibility = Visibility.Collapsed };
    private readonly SettingsOfficialPlugins _official;
    private CancellationTokenSource? _operation;
    private ContentDialog? _dialog;
    private string? _noticeKey;
    private bool _disposed;

    public SettingsPluginLibrary(PluginLibrary library, Func<Task<string?>> pick)
    {
        _library = library;
        _pick = pick;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        actions.Children.Add(_install);
        actions.Children.Add(_cancel);
        Children.Add(new SettingsRow(Symbol.Add, "PluginInstall", actions));
        _notice.Margin = new Thickness(12, 8, 12, 8);
        Children.Add(_notice);
        _official = new SettingsOfficialPlugins(library);
        Children.Add(_official);
        _install.Click += async (_, _) => await ImportAsync();
        _cancel.Click += (_, _) => _operation?.Cancel();
        LocalizedUI.Bind(this, () => { if (_noticeKey is { } key) _notice.Message = Localization.Get(key); });
    }

    private async Task ImportAsync()
    {
        if (_operation is not null || _disposed) return;
        using var operation = _operation = new CancellationTokenSource();
        _install.IsEnabled = false;
        _cancel.Visibility = Visibility.Visible;
        _notice.Visibility = Visibility.Collapsed;
        try
        {
            await Task.Yield();
            var file = await _pick();
            if (file is null) return;
            using var prepared = await _library.Installer.PrepareAsync(file, token: operation.Token);
            try { await _library.Manager.InstallAsync(prepared, token: operation.Token); }
            catch (PluginAlreadyInstalledException)
            {
                if (!await ConfirmOverwriteAsync()) return;
                await _library.Manager.InstallAsync(prepared, overwrite: true, token: operation.Token);
            }
            Show("PluginInstalledSuccess", InfoBarSeverity.Success);
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
            if (!_disposed) { _install.IsEnabled = true; _cancel.Visibility = Visibility.Collapsed; }
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

    public void Refresh() => _official.Refresh();
    public void Dispose() { _disposed = true; _dialog?.Hide(); _operation?.Cancel(); _official.Dispose(); }
}
