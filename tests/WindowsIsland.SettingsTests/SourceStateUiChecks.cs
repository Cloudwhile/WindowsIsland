using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WindowsIsland.Components;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland;

// 使用私有设置与可控异步回调，不创建管理器或真实客户端监听。
internal static class SourceStateUiChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var output = Path.Combine(AppContext.BaseDirectory, "source-state-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var store = new SettingsStore(Path.Combine(output, "settings.json"));
        store.Save(new(SetupCompleted: true, SystemNotifications: false, WeChat: false, Telegram: true, Power: false));
        var enabledGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enabledCalls = 0;
        var retryCalls = 0;
        Exception? failure = null;
        var definition = BuiltInPluginDefinitions.Telegram;
        NotificationPluginStatus[] snapshot = [new(definition, "TelegramNotDetected", CanRetry: true)];
        var sources = new SettingsSources(store, error => failure = error,
            _ => { retryCalls++; return retryGate.Task; },
            async (_, enabled) =>
            {
                enabledCalls++;
                await enabledGate.Task;
                store.Save(store.Current with { Telegram = enabled });
            });
        var content = SettingsSection.Page(sources);
        var window = new Window { Content = content };
        try
        {
            sources.Refresh(snapshot, busy: false);
            window.AppWindow.Resize(new SizeInt32(640, 320));
            window.Activate();
            await UntilAsync(() => Tree(sources).OfType<ToggleSwitch>().Any());
            var toggle = Tree(sources).OfType<ToggleSwitch>().Single(item => AutomationProperties.GetAutomationId(item) == definition.ToggleId);
            var retry = Tree(sources).OfType<Button>().Single(item => AutomationProperties.GetAutomationId(item) == "SourceRetry." + definition.Id);
            var progress = Tree(sources).OfType<ProgressRing>().Single(item => AutomationProperties.GetAutomationId(item) == "SourceProgress." + definition.Id);
            var row = Tree(sources).OfType<SettingsLinkRow>().Single();
            check(toggle.IsOn && row.Header.Status == Localization.Get("TelegramNotDetected"),
                "An enabled source keeps its switch on while the connection reports client not detected");
            check(progress.Visibility == Visibility.Collapsed && !progress.IsActive && !progress.IsTabStop
                && progress.Width == 20 && progress.Height == 20 && AutomationProperties.GetName(progress) == Localization.Get("Busy"),
                "Source progress starts hidden, inactive and outside keyboard navigation with a localized accessibility name");

            toggle.IsOn = false;
            await UntilAsync(() => enabledCalls == 1);
            check(progress.Visibility == Visibility.Visible && progress.IsActive && !toggle.IsEnabled && !retry.IsEnabled,
                "A pending source toggle immediately shows progress and prevents another source operation");
            sources.Refresh(snapshot, busy: true);
            check(!toggle.IsEnabled && !retry.IsEnabled && progress.Visibility == Visibility.Visible && progress.IsActive,
                "Global busy disables both controls and preserves progress while a source toggle is pending");
            enabledGate.SetResult();
            await UntilAsync(() => !store.Current.Telegram && !toggle.IsOn && !progress.IsActive && progress.Visibility == Visibility.Collapsed);
            check(!progress.IsActive && progress.Visibility == Visibility.Collapsed,
                "Completing a source toggle stops and collapses its progress while global busy remains active");
            check(!toggle.IsEnabled && !retry.IsEnabled,
                "Completing a source toggle preserves the active global busy restriction");
            sources.Refresh(snapshot, busy: true);
            sources.Refresh(snapshot, busy: true);
            check(!toggle.IsEnabled && !retry.IsEnabled,
                "Repeated unchanged busy snapshots cannot re-enable controls after a toggle completes");
            sources.Refresh(snapshot, busy: false);
            check(toggle.IsEnabled && retry.IsEnabled && !toggle.IsOn,
                "Leaving global busy restores controls without changing the saved disabled preference");

            store.Save(store.Current with { Telegram = true });
            sources.Refresh(snapshot, busy: false);
            check(toggle.IsOn && retry.Visibility == Visibility.Visible, "An enabled disconnected source exposes its retry action");
            ((IInvokeProvider)new ButtonAutomationPeer(retry).GetPattern(PatternInterface.Invoke)).Invoke();
            await UntilAsync(() => retryCalls == 1);
            check(progress.Visibility == Visibility.Visible && progress.IsActive && !toggle.IsEnabled && !retry.IsEnabled
                && row.Header.Status == Localization.Get("TelegramNotDetected"),
                "A pending source retry shows progress while retaining its connection result and blocking repeated operations");
            sources.Refresh(snapshot, busy: true);
            check(!toggle.IsEnabled && !retry.IsEnabled && progress.Visibility == Visibility.Visible && progress.IsActive,
                "Global busy disables both controls and preserves progress while source retry is pending");
            retryGate.SetResult();
            await UntilAsync(() => !progress.IsActive && progress.Visibility == Visibility.Collapsed);
            check(!progress.IsActive && progress.Visibility == Visibility.Collapsed,
                "Completing source retry stops and collapses its progress without ending global busy");
            check(!toggle.IsEnabled && !retry.IsEnabled,
                "Completing source retry preserves the active global busy restriction");
            sources.Refresh(snapshot, busy: true);
            sources.Refresh(snapshot, busy: true);
            check(!toggle.IsEnabled && !retry.IsEnabled,
                "Repeated unchanged busy snapshots preserve disabled controls after retry completes");
            sources.Refresh(snapshot, busy: false);
            check(toggle.IsEnabled && retry.IsEnabled && toggle.IsOn
                && row.Header.Status == Localization.Get("TelegramNotDetected") && failure is null,
                "Leaving busy restores actions and keeps enabled preference separate from connection failure");

            check(!HasSeparator(row), "A one-source list hides its last separator before any page reload");
            var power = new NotificationPluginStatus(BuiltInPluginDefinitions.Power, "Off");
            sources.Refresh([.. snapshot, power], busy: false);
            var powerRow = sources.Children.OfType<SettingsLinkRow>().Single(item => !ReferenceEquals(item, row));
            check(HasSeparator(row) && !HasSeparator(powerRow),
                "Adding a source restores the former last separator and hides the new last separator");
            sources.Refresh(snapshot, busy: false);
            check(sources.Children.OfType<SettingsLinkRow>().Single() == row && !HasSeparator(row),
                "Removing the last source retains the existing row and hides its separator immediately");
            sources.Refresh([.. snapshot, power], busy: false);
            powerRow = sources.Children.OfType<SettingsLinkRow>().Single(item => !ReferenceEquals(item, row));
            sources.Refresh([power], busy: false);
            check(sources.Children.OfType<SettingsLinkRow>().Single() == powerRow && !HasSeparator(powerRow),
                "Removing an earlier source leaves the remaining last separator hidden");
            sources.Refresh([power, .. snapshot], busy: false);
            var appended = sources.Children.OfType<SettingsLinkRow>().Last();
            check(HasSeparator(powerRow) && !HasSeparator(appended),
                "Appending after a removal restores the current former last separator");
            window.Content = null;
            window.Content = content;
            await Task.Delay(50);
            check(HasSeparator(powerRow) && !HasSeparator(appended),
                "Reloading the page preserves intermediate link separators and hides only its final row");
            sources.Refresh([], busy: false);
            check(!sources.Children.OfType<SettingsLinkRow>().Any(), "Removing every source leaves no stale rows or separators");
        }
        finally
        {
            enabledGate.TrySetCanceled();
            retryGate.TrySetCanceled();
            sources.Dispose();
            window.Close();
        }
    }

    private static bool HasSeparator(SettingsLinkRow row) => row.Header.Children.OfType<Border>().Single().Visibility == Visibility.Visible;

    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var item in Tree(VisualTreeHelper.GetChild(root, index))) yield return item;
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The isolated source control did not reach its expected state.");
            await Task.Delay(25);
        }
    }
}
