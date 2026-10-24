using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WindowsIsland.Components;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland;

internal static class OverviewUiChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var store = new SettingsStore(Path.Combine(AppContext.BaseDirectory, "verification-overview-settings.json"));
        store.Save(new(SetupCompleted: true, SystemNotifications: true, WeChat: true, Telegram: false, Power: false));
        var sources = new NotificationPluginStatus[]
        {
            new(BuiltInPluginDefinitions.System, "NeedsSetup"), new(BuiltInPluginDefinitions.WeChat, "Connected"),
            new(BuiltInPluginDefinitions.Telegram, "Off"), new(BuiltInPluginDefinitions.Power, "Off")
        };
        var pending = NewQuery();
        var queries = 0;
        using var service = new UpdateCheckService(store, (_, token) =>
        {
            Interlocked.Increment(ref queries);
            return pending.Task.WaitAsync(token);
        });
        var window = new SettingsWindow(store, service, () => new(NotificationAccess.NeedsRegistration, false, sources),
            () => Task.CompletedTask, () => Task.CompletedTask, () => { }, () => { }, verification: true);
        var page = Tree((DependencyObject)window.Content).OfType<SettingsPage>().Single();
        try
        {
            window.Activate();
            await Until(() => Tree(page).OfType<SettingsOverview>().Any());
            check(Text(page, "OverviewSourceCount").Text == "1 / 4"
                && Text(page, "OverviewSourceCount.Detail").Text == Localization.Format("OverviewEnabledSources", 2)
                && Tree(page).OfType<InfoBar>().Single(bar => Id(bar) == "OverviewSystemStatus").IsOpen,
                "An unconfigured system source does not hide a separately connected WeChat source or confuse enabled and running counts");
            check(!Tree(page).OfType<TextBlock>().Any(text => Visible(text) && Id(text) == "OverviewSourceHealth"),
                "The optional unconfigured system source is explained once without an unrelated-source failure warning");
            sources[1] = new(BuiltInPluginDefinitions.WeChat, "WeChatConnectFailed", CanRetry: true);
            page.Refresh();
            check(Text(page, "OverviewSourceCount").Text == "0 / 4"
                && Text(page, "OverviewSourceHealth").Text == Localization.Format("OverviewSourceFailures", 1),
                "Overview distinguishes a partial source failure and provides a source-management action");
            Invoke(Button(page, "OverviewInspectSources"));
            await Until(() => Tree(page).OfType<SettingsSources>().Any());
            var wechat = Tree(page).OfType<ToggleSwitch>().Single(toggle => Id(toggle) == BuiltInPluginDefinitions.WeChat.ToggleId);
            check(wechat.IsOn && wechat.IsEnabled, "A failed connection stays enabled and manageable independently of system notification setup");
            Select(page, "overview");
            await Until(() => Tree(page).OfType<SettingsOverview>().Any());
            Invoke(Button(page, "OverviewUpdates"));
            await Until(() => Volatile.Read(ref queries) == 1 && service.State.Busy && Tree(page).OfType<SettingsUpdates>().Any());
            check(!Button(page, "CheckForUpdates").IsEnabled,
                "The overview check-updates action starts the shared query and opens its existing progress page");
            Select(page, "overview");
            await Until(() => Tree(page).OfType<SettingsOverview>().Any());
            page.Refresh();
            check(Text(page, "OverviewVersion.Detail").Text == Localization.Get("UpdateChecking"),
                "Overview presents the shared update query's loading result");
            pending.SetException(new HttpRequestException("Fixed offline fixture"));
            await Until(() => !service.State.Busy && service.State.ErrorKey is not null);
            page.Refresh();
            check(Text(page, "OverviewVersion.Detail").Text == Localization.Get(service.State.ErrorKey!),
                "Overview presents the actual shared network failure without maintaining another update state");
            pending = NewQuery();
            var query = service.CheckAsync();
            await Until(() => Volatile.Read(ref queries) == 2);
            pending.SetResult(null);
            await query;
            page.Refresh();
            check(Text(page, "OverviewVersion.Detail").Text == Localization.Get("UpdateNone")
                && Text(page, "OverviewVersion").Text == ReleaseVersion.Current.Tag,
                "Overview recovers after a failed check and displays the running application's actual version");
        }
        finally { window.Close(); }
    }

    private static TaskCompletionSource<GitHubRelease?> NewQuery() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Id(DependencyObject element) => AutomationProperties.GetAutomationId(element);
    private static bool Visible(FrameworkElement element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement { Visibility: Visibility.Collapsed }) return false;
        return true;
    }
    private static TextBlock Text(DependencyObject root, string id) => Tree(root).OfType<TextBlock>().Single(text => Id(text) == id && Visible(text));
    private static Button Button(DependencyObject root, string id) => Tree(root).OfType<Button>().Single(button => Id(button) == id);
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static void Select(SettingsPage page, string id)
    {
        var navigation = Tree(page).OfType<NavigationView>().Single();
        navigation.SelectedItem = navigation.MenuItems.Concat(navigation.FooterMenuItems).OfType<NavigationViewItem>().Single(item => item.Tag is string tag && tag == id);
    }
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var element in Tree(VisualTreeHelper.GetChild(root, index))) yield return element;
    }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("The overview fixture did not reach the expected state.");
            await Task.Delay(30);
        }
    }
}
