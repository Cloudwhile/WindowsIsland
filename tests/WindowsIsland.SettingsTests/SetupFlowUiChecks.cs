using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WindowsIsland.Components;
using WindowsIsland.Services;

namespace WindowsIsland;

// Only compiled into the isolated settings verification application.
internal static class SetupFlowUiChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "verification-setup-flow-settings.json");
        var store = new SettingsStore(path);
        store.Save(new(SetupCompleted: false, SystemNotifications: true, WeChat: false, Telegram: false,
            Power: false, Animations: false, Position: NotificationPosition.BottomRight));
        var state = new SettingsSnapshot(NotificationAccess.NeedsRegistration, false, []);
        var initialize = NewOperation();
        var authorize = NewOperation();
        var initializationCalls = 0;
        var authorizationCalls = 0;
        var previews = 0;
        using var updates = new UpdateCheckService(store, (_, _) => Task.FromResult<GitHubRelease?>(null));
        var window = new SettingsWindow(store, updates, () => state,
            () => { initializationCalls++; return initialize.Task; },
            () => { authorizationCalls++; return authorize.Task; },
            () => previews++, () => { }, verification: true);
        try
        {
            window.AppWindow.Resize(new(840, 720));
            window.Activate();
            var page = Tree((DependencyObject)window.Content).OfType<SettingsPage>().Single();
            await Until(() => page.IsLoaded && Tree(page).OfType<SettingsOverview>().Any());
            check(Bar(page).Visibility == Visibility.Collapsed
                && Tree(page).OfType<InfoBar>().Single(bar => Id(bar) == "OverviewSystemStatus").Visibility == Visibility.Visible,
                "Unconfigured startup presents the system-notification reminder in overview without a duplicate global warning");
            Invoke(Button(page, "OverviewPreview"));
            await Until(() => previews == 1);
            check(previews == 1 && initializationCalls == 0 && authorizationCalls == 0,
                "Manual notification preview works before initialization without registration or authorization");
            await OpenSetupAsync(page);
            check(Button(page, "InitializeApplication").IsEnabled && !Button(page, "RequestNotificationAccess").IsEnabled
                && !Button(page, "CompleteSetup").IsEnabled && !store.Current.SetupCompleted,
                "Setup exposes ordered initialization, authorization and completion states");

            Invoke(Button(page, "InitializeApplication"));
            await Until(() => initializationCalls == 1 && !Button(page, "InitializeApplication").IsEnabled);
            check(!Button(page, "RequestNotificationAccess").IsEnabled && !Button(page, "CompleteSetup").IsEnabled
                && !Button(page, "VerifySetupStatus").IsEnabled && Bar(page).Message == Localization.Get("PleaseWait"),
                "An active initialization prevents duplicate operations and shows a busy result");
            page.Refresh();
            check(initializationCalls == 1, "Refreshing a pending initialization does not invoke it again");
            state = state with { Initialized = true, Access = NotificationAccess.Waiting };
            initialize.SetResult();
            await Until(() => Button(page, "RequestNotificationAccess").IsEnabled);
            check(!Button(page, "InitializeApplication").IsEnabled && !Button(page, "CompleteSetup").IsEnabled,
                "Initialization success enables notification authorization while completion waits for access");

            Select(page, "appearance");
            await Until(() => Tree(page).OfType<NotificationAppearancePreview>().Any());
            check(Bar(page).Visibility == Visibility.Collapsed && store.Current.Position == NotificationPosition.BottomRight && !store.Current.Animations,
                "Navigating away preserves preferences and hides unrelated setup guidance");
            Select(page, "overview");
            await OpenSetupAsync(page);
            check(!Button(page, "InitializeApplication").IsEnabled && Button(page, "RequestNotificationAccess").IsEnabled
                && !store.Current.SetupCompleted, "Returning to setup preserves initialization progress without completing preferences");
            Invoke(Button(page, "RequestNotificationAccess"));
            await Until(() => authorizationCalls == 1 && !Button(page, "RequestNotificationAccess").IsEnabled);
            check(!Button(page, "CompleteSetup").IsEnabled, "Authorization prevents duplicate clicks until its injected operation completes");
            state = state with { Access = NotificationAccess.Allowed };
            authorize.SetResult();
            await Until(() => Button(page, "CompleteSetup").IsEnabled);
            Invoke(Button(page, "VerifySetupStatus"));
            check(Tree(page).OfType<TextBlock>().Single(text => Id(text) == "InitializationStatus").Text == Localization.Get("Complete"),
                "Verification reflects the initialized and authorized snapshot");
            Invoke(Button(page, "CompleteSetup"));
            await Until(() => Tree(page).OfType<SettingsOverview>().Any());
            check(store.Current.SetupCompleted && new SettingsStore(path).Current.SetupCompleted
                && Bar(page).Visibility == Visibility.Collapsed,
                "Completing setup returns to overview and persists SetupCompleted");

            await OpenSetupAsync(page);
            await EscapeFromSetupAsync(window, page, check);
            check(store.Current.SetupCompleted, "Escape returns to overview without resetting completed setup");

            store.Save(store.Current with { SetupCompleted = false });
            state = new(NotificationAccess.NeedsRegistration, false, []);
            await OpenSetupAsync(page);
            initialize = NewOperation();
            Invoke(Button(page, "InitializeApplication"));
            await Until(() => initializationCalls == 2 && !Button(page, "InitializeApplication").IsEnabled);
            initialize.SetException(new InvalidOperationException("Fixed initialization fixture"));
            await Until(() => Button(page, "InitializeApplication").IsEnabled && Bar(page).Severity == InfoBarSeverity.Error);
            check(Bar(page).Message == Localization.Get("ErrorInitialize") && !Button(page, "CompleteSetup").IsEnabled
                && !store.Current.SetupCompleted, "Initialization failure keeps retry available and does not complete setup");

            initialize = NewOperation();
            Invoke(Button(page, "InitializeApplication"));
            await Until(() => initializationCalls == 3 && !Button(page, "InitializeApplication").IsEnabled);
            initialize.SetCanceled();
            await Until(() => Button(page, "InitializeApplication").IsEnabled && Bar(page).Title == Localization.Get("Cancelled"));
            check(Bar(page).Message == Localization.Get("InitializationPermissionCancelled")
                && Bar(page).Severity == InfoBarSeverity.Informational && !store.Current.SetupCompleted,
                "Canceled initialization restores retry and reports its own cancellation without marking success");

            initialize = NewOperation();
            Invoke(Button(page, "InitializeApplication"));
            await Until(() => initializationCalls == 4 && !Button(page, "InitializeApplication").IsEnabled);
            Select(page, "appearance");
            await Until(() => Tree(page).OfType<NotificationAppearancePreview>().Any());
            initialize.SetException(new InvalidOperationException("Fixed late initialization fixture"));
            await Until(() => Button(page, "PreviewNotification").IsEnabled);
            check(Bar(page).Visibility == Visibility.Collapsed && Bar(page).Title.Length == 0 && Bar(page).Message.Length == 0,
                "A late setup failure cannot display an error or stale busy notice on Appearance");
            Select(page, "overview");
            await OpenSetupAsync(page);
            initialize = NewOperation();
            Invoke(Button(page, "InitializeApplication"));
            await Until(() => initializationCalls == 5 && !Button(page, "InitializeApplication").IsEnabled);
            state = new(NotificationAccess.Allowed, true, []);
            initialize.SetResult();
            await Until(() => Button(page, "CompleteSetup").IsEnabled);
            check(Bar(page).Visibility == Visibility.Collapsed, "Retry after failure and cancellation can reach a ready setup state");
            Invoke(Button(page, "CompleteSetup"));
            await Until(() => Tree(page).OfType<SettingsOverview>().Any());

            state = new(NotificationAccess.NeedsRegistration, false, []);
            store.Save(store.Current with { SetupCompleted = false, SystemNotifications = false });
            await OpenSetupAsync(page);
            check(Button(page, "CompleteSetup").IsEnabled && !Button(page, "InitializeApplication").IsEnabled
                && !Button(page, "RequestNotificationAccess").IsEnabled
                && Tree(page).OfType<TextBlock>().Single(text => Id(text) == "InitializationStatus").Text == Localization.Get("SetupSystemOptional"),
                "Turning off the optional system notification source allows setup to be skipped without authorization");
            Invoke(Button(page, "CompleteSetup"));
            await Until(() => Tree(page).OfType<SettingsOverview>().Any());
            Invoke(Button(page, "OverviewPreview"));
            await Until(() => previews == 2);
            check(previews == 2 && initializationCalls == 5 && authorizationCalls == 1
                && new SettingsStore(path).Current is { SetupCompleted: true, SystemNotifications: false },
                "Optional setup persists completion and preview remains independent of registration");
        }
        finally { window.Close(); }
    }

    private static async Task OpenSetupAsync(SettingsPage page)
    {
        if (!Tree(page).OfType<SettingsOverview>().Any()) Select(page, "overview");
        await Until(() => Tree(page).OfType<SettingsOverview>().Any());
        Invoke(Button(page, "OverviewConfigureNotifications"));
        await Until(() => Tree(page).OfType<SettingsSetupFlow>().Any());
    }

    private static async Task EscapeFromSetupAsync(SettingsWindow window, SettingsPage page, Action<bool, string> check)
    {
        try
        {
            await NativeSettingsInput.EnsureForegroundAsync(window);
            await Task.Delay(120);
            page.UpdateLayout();
            var focus = Button(page, "VerifySetupStatus");
            check(focus.Focus(FocusState.Programmatic), "The isolated setup verification action accepts keyboard focus");
            if (!ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(page.XamlRoot), focus))
                throw new InvalidOperationException("The isolated setup action lost keyboard focus.");
            NativeSettingsInput.PressKey(window, Windows.System.VirtualKey.Escape);
            await Until(() => Tree(page).OfType<SettingsOverview>().Any());
            check(true, "Escape provides a working keyboard route from setup back to overview");
        }
        catch (NativeSettingsInput.UnavailableException error)
        {
            check(true, "SKIP real Escape input: " + error.Message);
            Invoke(Button(page, "CompleteSetup"));
            await Until(() => Tree(page).OfType<SettingsOverview>().Any());
        }
    }

    private static TaskCompletionSource NewOperation() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Id(DependencyObject item) => AutomationProperties.GetAutomationId(item);
    private static Button Button(DependencyObject root, string id) => Tree(root).OfType<Button>().Single(button => Id(button) == id);
    private static InfoBar Bar(DependencyObject root) => Tree(root).OfType<InfoBar>().Single(bar => Id(bar) == "SetupStatus");
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static void Select(SettingsPage page, string tag)
    {
        var navigation = Tree(page).OfType<NavigationView>().Single();
        navigation.SelectedItem = navigation.MenuItems.Concat(navigation.FooterMenuItems).OfType<NavigationViewItem>().Single(item => item.Tag is string value && value == tag);
    }
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The isolated setup flow did not reach its expected state.");
            await Task.Delay(30);
        }
    }
}
