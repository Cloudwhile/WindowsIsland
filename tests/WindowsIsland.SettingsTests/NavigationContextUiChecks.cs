using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using WindowsIsland.Components;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland;

/// <summary>使用纯本地伪来源验证列表导航上下文；不创建或启动任何真实来源。</summary>
internal static class NavigationContextUiChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "verification-navigation-context", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        var store = new SettingsStore(path);
        store.Save(new(SetupCompleted: true, SystemNotifications: false, WeChat: false, Telegram: false,
            Power: false, Animations: false, Language: Localization.Language));
        var definitions = Enumerable.Range(1, 12).Select(index => Definition(store, directory, index)).ToArray();
        var changes = new List<(string Id, bool Enabled)>();
        Task SetEnabledAsync(string id, bool enabled)
        {
            store.SetPluginBoolean(id, "Host.Enabled", enabled);
            store.Save(store.Current);
            changes.Add((id, enabled));
            return Task.CompletedTask;
        }
        SettingsSnapshot Snapshot() => new(NotificationAccess.Allowed, true,
            definitions.Select(definition => new NotificationPluginStatus(definition,
                definition.IsEnabled(store.Current) ? "On" : "Off")).ToArray());
        using var updates = new UpdateCheckService(store);
        var window = new SettingsWindow(store, updates, Snapshot, () => Task.CompletedTask,
            () => Task.CompletedTask, () => { }, () => { }, verification: true,
            retrySource: _ => Task.CompletedTask, setSourceEnabled: SetEnabledAsync,
            removePlugin: _ => Task.CompletedTask, rescanPlugins: () => Task.CompletedTask);
        try
        {
            window.AppWindow.Resize(new SizeInt32(520, 480));
            window.Activate();
            var page = Tree((FrameworkElement)window.Content).OfType<SettingsPage>().Single();
            page.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
                File.AppendAllText(Path.Combine(directory, "keys.log"), $"Root down {args.Key} source={args.OriginalSource?.GetType().Name} focus={FocusManager.GetFocusedElement(page.XamlRoot)?.GetType().Name} handled={args.Handled}\n")), true);
            page.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler((_, args) =>
                File.AppendAllText(Path.Combine(directory, "keys.log"), $"Root up {args.Key} handled={args.Handled}\n")), true);
            await SelectAsync(page, "sources");
            var sources = Tree(page).OfType<SettingsSources>().Single();
            var rows = Tree(sources).OfType<SettingsLinkRow>().ToArray();
            var scroll = Find<ScrollViewer>(page, "SettingsContentScroll")!;
            check(rows.Length == 12 && scroll.ScrollableHeight > 100,
                "Twelve isolated fake sources create a real scrollable list in the 520 by 480 settings window");
            scroll.ChangeView(null, Math.Min(500, scroll.ScrollableHeight * 0.55), null, true);
            await SettleAsync(page);
            check(scroll.VerticalOffset > 20, "The context fixture starts at a nonzero source-list scroll position");
            var viewport = Bounds(scroll, page);
            var open = rows.SelectMany(row => Tree(row).OfType<IconActionButton>())
                .Where(button => AutomationProperties.GetAutomationId(button).EndsWith(".Open", StringComparison.Ordinal))
                .Where(button =>
                {
                    var bounds = Bounds(button, page);
                    return bounds.Top >= viewport.Top + 2 && bounds.Bottom <= viewport.Bottom - 2;
                })
                .OrderBy(button => Math.Abs(Bounds(button, page).Y - (viewport.Y + viewport.Height / 2)))
                .FirstOrDefault();
            check(open is not null, "A source details action is fully visible inside the scrolled list viewport");
            var action = open!;
            var nativeAvailable = true;
            try { await NativeSettingsInput.EnsureForegroundAsync(window); }
            catch (NativeSettingsInput.UnavailableException error)
            {
                nativeAvailable = false;
                check(true, "SKIP foreground-dependent context input: " + error.Message);
            }
            var focusRequested = action.Focus(FocusState.Programmatic);
            var validateFocus = focusRequested && ReferenceEquals(FocusManager.GetFocusedElement(page.XamlRoot), action);
            if (nativeAvailable) check(validateFocus, "The visible source details action accepts focus before navigation");
            else if (!validateFocus) check(true, "SKIP focus restoration assertion because the input desktop did not grant focus");
            var offset = scroll.VerticalOffset;
            var originalList = scroll.Content;
            RecordContext(directory, "before details", page, scroll);
            Invoke(action);
            await UntilAsync(() => Tree(page).OfType<SettingsPluginDetails>().Any(), "Source details did not open.");
            await SettleAsync(page);
            RecordContext(directory, "inside details", page, scroll);
            var back = Find<IconActionButton>(page, "BackToPluginList")!;
            check(back.Visibility == Visibility.Visible && back.ActualWidth > 0,
                "Source details expose a reachable native back action in the small window");
            Invoke(back);
            await UntilAsync(() => Tree(page).OfType<SettingsSources>().Any()
                && Math.Abs(scroll.VerticalOffset - offset) <= 2, "Source-list scroll context was not restored after returning.");
            RecordContext(directory, "after details return", page, scroll);
            check(ReferenceEquals(scroll.Content, originalList)
                && ReferenceEquals(Tree(page).OfType<SettingsSources>().Single(), sources)
                && Tree(sources).OfType<SettingsLinkRow>().SequenceEqual(rows),
                "Returning from source details restores the same list container and source-row instances");
            check(offset > 20 && Math.Abs(scroll.VerticalOffset - offset) <= 2,
                "Returning from source details restores the original nonzero scroll offset within two DIP");
            if (validateFocus)
            {
                try
                {
                    await UntilAsync(() => ReferenceEquals(FocusManager.GetFocusedElement(page.XamlRoot), action),
                        "The original source details action did not regain focus after returning.");
                }
                catch { RecordContext(directory, "focus restoration failure", page, scroll); throw; }
                check(true, "Returning from source details restores focus to the exact original action");
            }
            await SelectAsync(page, "appearance");
            await SelectAsync(page, "sources");
            await UntilAsync(() => Math.Abs(scroll.VerticalOffset - offset) <= 2,
                "The source-list offset was reset by navigating through appearance.");
            check(ReferenceEquals(scroll.Content, originalList) && scroll.VerticalOffset > 20
                && Math.Abs(scroll.VerticalOffset - offset) <= 2,
                "Navigating through appearance preserves the source list and its nonzero scroll position");
            RecordContext(directory, "after appearance round trip", page, scroll);
            check(changes.Count == 0, "Opening details and switching pages do not toggle any fake source");

            const string actionPrefix = "Plugin.", actionSuffix = ".Open";
            var id = AutomationProperties.GetAutomationId(action)[actionPrefix.Length..^actionSuffix.Length];
            var definition = definitions.Single(definition => definition.Id == id);
            var toggle = Find<ToggleSwitch>(sources, definition.ToggleId)!;
            try
            {
                await NativeSettingsInput.EnsureForegroundAsync(window);
                toggle.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                await SettleAsync(page);
                check(toggle.Focus(FocusState.Keyboard), "The fake source switch accepts keyboard focus");
                NativeSettingsInput.PressKey(window, VirtualKey.Tab);
                await UntilAsync(() => ReferenceEquals(FocusManager.GetFocusedElement(page.XamlRoot), action),
                    "A real Tab key did not reach the same source row's details action.");
                await Task.Delay(100);
                RecordContext(directory, "after Tab settles", page, scroll);
                check(ReferenceEquals(FocusManager.GetFocusedElement(page.XamlRoot), action), "The details action retains keyboard focus after Tab dispatch completes");
                check(true, "A real Tab key moves from the source switch to its details action");
                var keyboardOffset = scroll.VerticalOffset;
                RecordContext(directory, "before keyboard details", page, scroll);
                action.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
                    File.AppendAllText(Path.Combine(directory, "keys.log"), $"Down {args.Key} handled={args.Handled}\n")), true);
                action.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler((_, args) =>
                    File.AppendAllText(Path.Combine(directory, "keys.log"), $"Up {args.Key} handled={args.Handled}\n")), true);
                action.Click += (_, _) => File.AppendAllText(Path.Combine(directory, "keys.log"), "Click\n");
                NativeSettingsInput.PressKey(window, VirtualKey.Enter);
                await UntilAsync(() => Tree(page).OfType<SettingsPluginDetails>().Any(),
                    "A real Enter key did not open source details.");
                check(Find<IconActionButton>(page, "BackToPluginList") is { Visibility: Visibility.Visible },
                    "A real Enter key opens source details with a visible return action");
                NativeSettingsInput.PressKey(window, VirtualKey.Escape);
                await UntilAsync(() => Tree(page).OfType<SettingsSources>().Any()
                    && Math.Abs(scroll.VerticalOffset - keyboardOffset) <= 2
                    && ReferenceEquals(FocusManager.GetFocusedElement(page.XamlRoot), action),
                    "A real Escape key did not restore the source list, scroll offset and original focus.");
                check(ReferenceEquals(scroll.Content, originalList),
                    "A real Escape key returns to the same source list with its scroll context and focused details action");
                RecordContext(directory, "after real Escape", page, scroll);
                check(toggle.Focus(FocusState.Keyboard), "The returned source switch accepts keyboard focus");
                var before = definition.IsEnabled(store.Current);
                NativeSettingsInput.PressKey(window, VirtualKey.Space);
                await UntilAsync(() => changes.Count == 1 && toggle.IsOn != before && toggle.IsEnabled,
                    "A real Space key did not update the fake source switch.");
                check(changes[0] == (id, !before)
                    && new SettingsStore(path).GetPluginBoolean(id, "Host.Enabled", before) == !before
                    && definitions.Where(other => other.Id != id).All(other => !other.IsEnabled(store.Current)),
                    "A real Space key invokes the fake enable callback once and persists only that source in the isolated store");
            }
            catch (NativeSettingsInput.UnavailableException error)
            {
                check(true, "SKIP real Space/Tab/Enter/Escape navigation input: " + error.Message);
            }
        }
        finally { window.Close(); }
    }

    private static NotificationPluginDefinition Definition(SettingsStore store, string directory, int index)
    {
        var id = $"wini_plugin_navigation_fixture_{index:00}";
        var title = $"Context fixture source {index:00} with a longer title for narrow-window navigation";
        var paths = new PluginPaths(Path.Combine(directory, "fake-plugins"));
        var manifest = new PluginManifest(id, title, "1.0.0", $"source-{index:00}.dll", "Fixture.UnusedSource",
            PluginContract.Version, [], Author: "Fixture author", SourceKind: PluginSourceKind.Manual);
        var code = paths.Code(PluginSourceKind.Manual, id);
        var external = new ExternalPlugin(paths, manifest, code);
        return BuiltInPluginDefinitions.Telegram with
        {
            Id = id,
            TitleKey = title,
            ToggleId = $"ContextSourceToggle{index:00}",
            IsEnabled = _ => store.GetPluginBoolean(id, "Host.Enabled", false),
            SetEnabled = (settings, _) => settings,
            Matches = _ => false,
            Metadata = new PluginMetadata(false, manifest.Version, Path.Combine(code, manifest.EntryAssembly), manifest.Author, external)
        };
    }

    private static async Task SelectAsync(SettingsPage page, string id)
    {
        var navigation = Tree(page).OfType<NavigationView>().Single();
        navigation.SelectedItem = navigation.MenuItems.Concat(navigation.FooterMenuItems)
            .OfType<NavigationViewItem>().Single(item => item.Tag is string section && section == id);
        await SettleAsync(page);
    }
    private static void RecordContext(string directory, string stage, SettingsPage page, ScrollViewer scroll)
    {
        var focused = FocusManager.GetFocusedElement(page.XamlRoot) as DependencyObject;
        var focus = focused is null ? "none" : AutomationProperties.GetAutomationId(focused);
        File.AppendAllText(Path.Combine(directory, "context.log"),
            $"{stage}: offset={scroll.VerticalOffset:F2}; focus={focus}; type={focused?.GetType().Name}{Environment.NewLine}");
    }
    private static async Task SettleAsync(SettingsPage page)
    {
        page.Refresh();
        await Task.Delay(180);
        page.UpdateLayout();
    }
    private static async Task UntilAsync(Func<bool> condition, string failure)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new InvalidOperationException(failure);
            await Task.Delay(25);
        }
    }
    private static T? Find<T>(DependencyObject root, string id) where T : FrameworkElement => Tree(root).OfType<T>()
        .SingleOrDefault(element => AutomationProperties.GetAutomationId(element) == id);
    private static Rect Bounds(FrameworkElement element, UIElement relative) => element.TransformToVisual(relative)
        .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button)
        .GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
