using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Components;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland;

public sealed partial class MainWindow
{
    private readonly CancellationTokenSource _pluginApprovalLifetime = new();
    private Func<PluginConsentDescriptor, Task<bool>>? _enableApprovalCallback;
    private ContentDialog? _pluginApprovalDialog;

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint GetPluginApprovalForegroundWindow();

    private void InitializePluginApprovals()
    {
        _enableApprovalCallback = ConfirmPluginEnableAsync;
        _plugins.ConfirmEnableAsync = _enableApprovalCallback;
    }

    private Task<bool> ConfirmPluginEnableAsync(PluginConsentDescriptor descriptor)
    {
        var snapshot = descriptor with
        {
            Permissions = descriptor.Permissions.ToArray(),
            Network = descriptor.Network is { } network ? network with
                { Origins = network.Origins.ToArray(), DataCategories = network.DataCategories.ToArray() } : null
        };
        return DispatchPluginApprovalAsync(() => PluginConsentDialog.CreateEnable(snapshot), CancellationToken.None);
    }

    private async Task<bool> DispatchPluginApprovalAsync(Func<ContentDialog> create, CancellationToken token)
    {
        var owner = _settingsWindow;
        if (_closed || owner is null || token.IsCancellationRequested || _pluginApprovalLifetime.IsCancellationRequested) return false;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _pluginApprovalLifetime.Token);
        var approvalToken = lifetime.Token;
        if (DispatcherQueue.HasThreadAccess) return await ShowPluginApprovalAsync(owner, create, approvalToken);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = approvalToken.Register(() => completion.TrySetResult(false));
        if (!DispatcherQueue.TryEnqueue(async () => completion.TrySetResult(await ShowPluginApprovalAsync(owner, create, approvalToken)))) return false;
        return await completion.Task;
    }

    private async Task<bool> ShowPluginApprovalAsync(SettingsWindow owner, Func<ContentDialog> create, CancellationToken token)
    {
        if (token.IsCancellationRequested || _pluginApprovalDialog is not null
            || !CanShowPluginApproval(owner) || owner.Content is not FrameworkElement { XamlRoot: { } root }) return false;
        ContentDialog? dialog = null;
        var ownerAppWindow = owner.AppWindow;
        if (ownerAppWindow is null) return false;
        var cancelled = false;
        void Cancel()
        {
            cancelled = true;
            try { dialog?.Hide(); } catch (COMException) { }
        }
        void Closed(object sender, WindowEventArgs args) => Cancel();
        void Activated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated) Cancel();
        }
        void WindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (!CanShowPluginApproval(owner, requireForeground: false)) { Cancel(); return; }
            if (dialog?.Content is ScrollViewer scroll) scroll.MaxHeight = Math.Max(80, Math.Min(440, root.Size.Height - 200));
        }
        try
        {
            dialog = _pluginApprovalDialog = create();
            dialog.XamlRoot = root;
            if (dialog.Content is ScrollViewer scroll) scroll.MaxHeight = Math.Max(80, Math.Min(440, root.Size.Height - 200));
            owner.Closed += Closed;
            owner.Activated += Activated;
            ownerAppWindow.Changed += WindowChanged;
            using var cancellation = token.Register(() => DispatcherQueue.TryEnqueue(Cancel));
            if (token.IsCancellationRequested || !CanShowPluginApproval(owner)) return false;
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary && !cancelled && !token.IsCancellationRequested
                && CanShowPluginApproval(owner);
        }
        catch (Exception error)
        {
            Diagnostics.Capture(DiagnosticArea.Application, error, "PluginConsent");
            return false;
        }
        finally
        {
            try
            {
                owner.Closed -= Closed;
                owner.Activated -= Activated;
                ownerAppWindow.Changed -= WindowChanged;
            }
            catch (COMException) { }
            if (ReferenceEquals(_pluginApprovalDialog, dialog)) _pluginApprovalDialog = null;
        }
    }

    private bool CanShowPluginApproval(SettingsWindow owner, bool requireForeground = true)
    {
        try
        {
            if (_closed || !ReferenceEquals(_settingsWindow, owner) || owner.AppWindow is not { IsVisible: true } appWindow
                || appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }
                || owner.Content is not FrameworkElement { IsLoaded: true }) return false;
            return !requireForeground || GetPluginApprovalForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(owner);
        }
        catch (COMException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private void ReleasePluginApprovals()
    {
        _pluginApprovalLifetime.Cancel();
        try { _pluginApprovalDialog?.Hide(); } catch (COMException) { }
        if (ReferenceEquals(_plugins.ConfirmEnableAsync, _enableApprovalCallback)) _plugins.ConfirmEnableAsync = null;
    }
}
