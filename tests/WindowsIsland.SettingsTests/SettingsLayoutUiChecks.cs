using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using WindowsIsland.Components;
using WindowsIsland.Services;

namespace WindowsIsland;

// This file is compiled only into the isolated native settings verification application.
internal static class SettingsLayoutUiChecks
{
    private static readonly (string Id, string Title)[] Sections =
    [
        ("overview", "Overview"), ("sources", "Sources"), ("appearance", "Appearance"),
        ("behavior", "NotificationBehavior"), ("updates", "Updates"), ("diagnostics", "DiagnosticsTitle")
    ];

    public static async Task RunAsync(SettingsWindow window, SettingsPage page, SettingsStore store, Action<bool, string> check)
    {
        var root = (FrameworkElement)window.Content;
        var original = store.Current;
        var language = Localization.Language;
        var theme = root.RequestedTheme;
        var size = window.AppWindow.Size;
        var screenshots = Path.Combine(AppContext.BaseDirectory, "settings-layout");
        Directory.CreateDirectory(screenshots);
        try
        {
            check(CurrentSection(page) == "overview", "Completed setup defaults to the overview page");
            var navigation = Tree(page).OfType<NavigationView>().Single();
            check(navigation.MenuItems.OfType<NavigationViewItemHeader>().Count() == 3,
                "Navigation separates runtime management, preferences and maintenance");
            check(!navigation.MenuItems.OfType<NavigationViewItem>().Any(item => item.Tag is "setup")
                && navigation.MenuItems.OfType<NavigationViewItem>().Any(item => item.Tag is "behavior"),
                "Initialization is an overview task and notification behavior has its own navigation route");
            check(ToolTipService.GetToolTip(navigation) is null
                && Tree(page).OfType<SettingsNavigation>().All(item => ToolTipService.GetToolTip(item) is null),
                "Navigation layout containers do not expose a redundant tooltip");

            foreach (var currentLanguage in new[] { "zh-CN", "en-US" })
                foreach (var currentTheme in new[] { ElementTheme.Light, ElementTheme.Dark })
                    foreach (var width in new[] { 520, 840, 900 })
                    {
                        Localization.Configure(currentLanguage);
                        root.RequestedTheme = currentTheme;
                        window.AppWindow.Resize(new SizeInt32(width, 760));
                        await SettleAsync(page);
                        if (width == 520) await CheckNarrowNavigationAsync(page, navigation, check,
                            $"{currentLanguage}/{currentTheme}/{width}");
                        else check(navigation.DisplayMode == NavigationViewDisplayMode.Expanded && navigation.IsPaneOpen
                            && navigation.MenuItems.OfType<NavigationViewItem>().All(item => item.ActualWidth >= 150),
                            $"Wide windows restore the expanded native pane and visible navigation labels: {currentLanguage}/{currentTheme}/{width}");
                        check(navigation.MenuItems.OfType<NavigationViewItemHeader>()
                            .Select(item => item.Content?.ToString()).SequenceEqual(new[]
                            {
                                Localization.Get("NavigationRuntime"), Localization.Get("NavigationPreferences"),
                                Localization.Get("NavigationMaintenance")
                            }), $"Navigation groups follow the active language: {currentLanguage}/{currentTheme}/{width}");
                        foreach (var section in Sections)
                        {
                            await SelectAsync(page, section.Id);
                            var context = $"{section.Id}/{currentLanguage}/{currentTheme}/{width}";
                            var header = Tree(page).OfType<SettingsHeader>().Single();
                            check(header.Title == Localization.Get(section.Title), "Page title follows the current route: " + context);
                            CheckBounds(page, header, check, context);
                            CheckIconActions(page, check, context);
                            if (section.Id == "behavior")
                                check(Find<ToggleSwitch>(page, "MuteDuringGamesToggle") is not null
                                    && Find<ToggleSwitch>(page, "MuteDuringFullScreenToggle") is not null
                                    && !Tree(page).OfType<SettingsSources>().Any(),
                                    "Notification behavior owns its controls without duplicating source management: " + context);
                            if (section.Id == "sources")
                                check(Find<ToggleSwitch>(page, "MuteDuringGamesToggle") is null,
                                    "Source management does not repeat notification preferences: " + context);
                            if (section.Id == "appearance") CheckPreviewBounds(page, check, context);
                            await CaptureAsync(window, Path.Combine(screenshots,
                                $"{section.Id}-{currentLanguage}-{currentTheme}-{width}.png"));
                        }
                    }

            await CheckAppearanceAsync(window, page, store, check);
            await CheckRefreshAsync(page, check);
            await CheckNoticeScopeAsync(check, screenshots);
            await CheckExistingDisplayDpiAsync(window, page, check);
        }
        finally
        {
            store.Save(original);
            Localization.Configure(language);
            root.RequestedTheme = theme;
            window.AppWindow.Resize(size);
            await SelectAsync(page, "sources");
            window.Activate();
        }
    }

    private static void CheckBounds(SettingsPage page, SettingsHeader header, Action<bool, string> check, string context)
    {
        var pageRect = new Rect(0, 0, page.ActualWidth, page.ActualHeight);
        var headerRect = Bounds(header, page);
        check(headerRect.Width > 0 && ContainsHorizontal(pageRect, headerRect)
            && headerRect.Y >= -1 && headerRect.Bottom <= pageRect.Bottom + 1, "The title and toolbar remain within the page: " + context);
        var scroll = Find<ScrollViewer>(page, "SettingsContentScroll")!;
        var viewport = Bounds(scroll, page);
        check(scroll.ActualWidth > 0 && scroll.ActualHeight > 0 && ContainsHorizontal(pageRect, viewport)
            && scroll.ExtentWidth <= scroll.ViewportWidth + 2, "Page content has no horizontal overflow: " + context);
        var content = (FrameworkElement)scroll.Content;
        var controls = Tree(content).OfType<FrameworkElement>().Where(element => IsVisible(element, content)
            && element.ActualWidth > 0 && element.ActualHeight > 0
            && !string.IsNullOrEmpty(AutomationProperties.GetAutomationId(element))
            && element is ButtonBase or ToggleSwitch or ComboBox or Slider or TextBox or InfoBar).ToArray();
        foreach (var control in controls)
        {
            var bounds = Bounds(control, page);
            // Vertically clipped rows are normal scroll content. Only inspect controls that intersect the viewport.
            if (bounds.Bottom <= viewport.Top || bounds.Top >= viewport.Bottom) continue;
            check(ContainsHorizontal(viewport, bounds), "Visible control fits its scroll viewport: "
                + context + "/" + AutomationProperties.GetAutomationId(control));
        }
        var title = Tree(header).OfType<TextBlock>().First(element => element.Text == header.Title);
        var titleRect = Bounds(title, page);
        var actions = Tree(header).OfType<IconActionButton>().Where(element => IsVisible(element, header)).ToArray();
        check(actions.All(action => Bounds(action, page).Left >= titleRect.Right - 1),
            "Header actions do not overlap the title: " + context);
    }

    private static void CheckIconActions(SettingsPage page, Action<bool, string> check, string context)
    {
        var actions = Tree(page).OfType<IconActionButton>().Where(element => IsVisible(element, page)).ToArray();
        check(actions.All(action => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(action))
                && !string.IsNullOrWhiteSpace(ToolTipService.GetToolTip(action)?.ToString())),
            "Visible icon actions retain localized tooltip configuration and accessibility names: " + context);
    }

    private static async Task CheckNarrowNavigationAsync(SettingsPage page, NavigationView navigation,
        Action<bool, string> check, string context)
    {
        var toggle = Tree(navigation).OfType<Button>().Single(button => button.Name == "TogglePaneButton");
        check(navigation.DisplayMode == NavigationViewDisplayMode.Compact && navigation.IsPaneToggleButtonVisible
            && IsVisible(toggle, page) && toggle.ActualWidth > 0 && toggle.ActualHeight > 0
            && ContainsHorizontal(new Rect(0, 0, page.ActualWidth, page.ActualHeight), Bounds(toggle, page)),
            "The narrow window retains a visible native navigation menu action: " + context);
        var expectedLabel = Localization.Get("ActionToggleNavigation");
        var actualName = AutomationProperties.GetName(toggle);
        var actualTip = ToolTipText(toggle);
        check(actualName == expectedLabel && actualTip == expectedLabel,
            "The native navigation menu action has localized tooltip configuration and an accessibility name: " + context
            + $"; name={actualName}; tooltip={actualTip}; expected={expectedLabel}");
        if (navigation.IsPaneOpen) Invoke(toggle);
        Invoke(toggle);
        await UntilAsync(() => navigation.IsPaneOpen);
        var sources = navigation.MenuItems.OfType<NavigationViewItem>().Single(item => item.Tag is "sources");
        check(IsVisible(sources, page) && sources.ActualWidth > 0 && sources.ActualHeight > 0,
            "Invoking the native menu reveals a reachable source navigation item: " + context);
        Invoke(toggle);
        await UntilAsync(() => !navigation.IsPaneOpen);
        await SettleAsync(page);
        check(AutomationProperties.GetName(toggle) == expectedLabel && ToolTipText(toggle) == expectedLabel,
            "Closing the native pane preserves the app-language navigation action label: " + context);
    }

    private static void CheckPreviewBounds(SettingsPage page, Action<bool, string> check, string context)
    {
        var preview = Find<NotificationAppearancePreview>(page, "NotificationAppearancePreview")!;
        var island = Find<Border>(preview, "AppearancePreviewIsland")!;
        var screen = (Canvas)VisualTreeHelper.GetParent(island);
        var bounds = Bounds(island, screen);
        check(preview.ActualWidth > 0 && bounds.X >= -1 && bounds.Y >= -1
            && bounds.Right <= screen.ActualWidth + 1 && bounds.Bottom <= screen.ActualHeight + 1,
            "The simulated island remains inside the display: " + context);
        var buttons = Tree(preview).OfType<ToggleButton>().Where(button =>
            AutomationProperties.GetAutomationId(button).StartsWith("NotificationPosition", StringComparison.Ordinal)).ToArray();
        check(buttons.Length == 8 && buttons.Count(button => button.IsChecked == true) == 1
            && buttons.All(button => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))),
            "Position choices retain eight stable accessible controls and one selection: " + context);
    }

    private static async Task CheckAppearanceAsync(SettingsWindow window, SettingsPage page, SettingsStore store, Action<bool, string> check)
    {
        await SelectAsync(page, "appearance");
        store.Save(store.Current with { Position = NotificationPosition.TopLeft, Animations = false });
        await SettleAsync(page);
        var picker = Find<NotificationPositionPicker>(page, "NotificationPositionPicker")!;
        var topLeft = Find<ToggleButton>(picker, "NotificationPositionTopLeft")!;
        topLeft.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        await SettleAsync(page);
        try
        {
            await NativeSettingsInput.EnsureForegroundAsync(window);
            check(topLeft.Focus(FocusState.Keyboard),
                "The isolated settings window receives keyboard focus before sending position keys");
            NativeSettingsInput.PressKey(window, Windows.System.VirtualKey.Right);
            await UntilAsync(() => store.Current.Position == NotificationPosition.TopCenter);
            NativeSettingsInput.PressKey(window, Windows.System.VirtualKey.Down);
            await UntilAsync(() => store.Current.Position == NotificationPosition.BottomCenter);
            var bottom = Find<ToggleButton>(picker, "NotificationPositionBottomCenter")!;
            check(bottom.IsChecked == true && ReferenceEquals(FocusManager.GetFocusedElement(page.XamlRoot), bottom),
                "Arrow keys select and focus positions, including movement across the unused center cell");
            var positionLabel = Find<TextBlock>(page, "AppearancePreviewPosition")!;
            check(positionLabel.Text == Localization.Format("AppearancePositionCurrent", NotificationPlacement.Label(store.Current.Position)),
                "Keyboard position changes update the simulation and current-position label immediately");
        }
        catch (NativeSettingsInput.UnavailableException error)
        {
            check(true, "SKIP real position keyboard input: " + error.Message);
        }
        foreach (var position in Enum.GetValues<NotificationPosition>())
        {
            var choice = Find<ToggleButton>(picker, "NotificationPosition" + position)!;
            if (choice.IsChecked != true)
                ((IToggleProvider)new ToggleButtonAutomationPeer(choice).GetPattern(PatternInterface.Toggle)).Toggle();
            await SettleAsync(page);
            check(store.Current.Position == position, "Accessible position selection saves immediately: " + position);
            CheckPreviewBounds(page, check, "position-" + position);
        }

        var slider = Find<Slider>(page, "BackgroundTransparencySlider")!;
        slider.Value = 65;
        await SettleAsync(page);
        var preview = Find<NotificationAppearancePreview>(page, "NotificationAppearancePreview")!;
        var surface = Tree(preview).OfType<Border>().Single(border => border.Resources.ContainsKey("PreviewAcrylic"));
        var effects = new Windows.UI.ViewManagement.UISettings().AdvancedEffectsEnabled
            && !new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        check(Math.Abs(store.Current.BackgroundOpacity - 0.35) < 0.001
            && Math.Abs(surface.Opacity - (effects ? 0.35 : 1)) < 0.001,
            "The transparency control saves immediately and updates the simulation with system-effects fallback");
        var acrylic = Find<ToggleSwitch>(page, "AcrylicToggle")!;
        acrylic.IsOn = false;
        check(!store.Current.Acrylic && surface.Background is SolidColorBrush,
            "Turning off acrylic immediately selects the solid material in the simulation");
        acrylic.IsOn = true;
        check(store.Current.Acrylic && (effects ? surface.Background is AcrylicBrush : surface.Background is SolidColorBrush),
            "Acrylic simulation follows the preference and the actual system-effects availability");
        var reloaded = new SettingsStore(Path.Combine(AppContext.BaseDirectory, "verification-settings.json"));
        check(reloaded.Current.Position == store.Current.Position
            && Math.Abs(reloaded.Current.BackgroundOpacity - store.Current.BackgroundOpacity) < 0.001
            && reloaded.Current.Acrylic == store.Current.Acrylic,
            "Position and material preferences survive reloading the isolated settings file");
        var action = Find<IconActionButton>(page, "PreviewActualNotification")!;
        check(action.IsEnabled && ToolTipService.GetToolTip(action)?.ToString() == Localization.Get("ActionPreviewActual"),
            "A separate accessible action previews the actual host notification");
        try
        {
            await NativeSettingsInput.HoverAsync(window, action);
            check(true, "Real pointer hover enters the notification-preview action and opens its native tooltip");
        }
        catch (NativeSettingsInput.UnavailableException error)
        {
            check(true, "SKIP real preview-action tooltip hover: " + error.Message);
        }
        CheckPreviewBounds(page, check, "keyboard-and-materials");
    }

    private static async Task CheckRefreshAsync(SettingsPage page, Action<bool, string> check)
    {
        await SelectAsync(page, "plugins");
        var before = Tree(page).OfType<SettingsLinkRow>().Where(row =>
            AutomationProperties.GetAutomationId(row).StartsWith("InstalledPlugin.", StringComparison.Ordinal))
            .ToDictionary(AutomationProperties.GetAutomationId);
        for (var index = 0; index < 10; index++) page.Refresh();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        for (var index = 0; index < 200; index++) page.Refresh();
        timer.Stop();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var after = Tree(page).OfType<SettingsLinkRow>().Where(row =>
            AutomationProperties.GetAutomationId(row).StartsWith("InstalledPlugin.", StringComparison.Ordinal))
            .ToDictionary(AutomationProperties.GetAutomationId);
        check(before.Count > 0 && before.Count == after.Count
            && before.All(entry => after.TryGetValue(entry.Key, out var row) && ReferenceEquals(row, entry.Value)),
            "Two hundred unchanged refreshes preserve the installed-plugin row instances");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "settings-layout-performance.json"), JsonSerializer.Serialize(new
        {
            RefreshCount = 200, ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
            AllocatedBytesOnUiThread = bytes, InstalledRows = before.Count
        }, new JsonSerializerOptions { WriteIndented = true }));
        check(true, $"Refresh measurements: 200 calls, {timer.Elapsed.TotalMilliseconds:F1} ms, {bytes} UI-thread allocated bytes");
        await SelectAsync(page, "sources");
        var sourceRows = Tree(page).OfType<SettingsLinkRow>().Count(row =>
            AutomationProperties.GetAutomationId(row).StartsWith("Plugin.", StringComparison.Ordinal));
        var visualNodes = Tree(page).Count();
        var rounds = new List<object>();
        for (var round = 1; round <= 3; round++)
        {
            for (var index = 0; index < 20; index++) page.Refresh();
            allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            timer.Restart();
            for (var index = 0; index < 200; index++) page.Refresh();
            timer.Stop();
            bytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            rounds.Add(new { Round = round, ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds, AllocatedBytesOnUiThread = bytes });
            check(true, $"Sources refresh round {round}: {sourceRows} rows, {visualNodes} visual nodes, "
                + $"warmup 20 + 200 calls, {timer.Elapsed.TotalMilliseconds:F2} ms, {bytes} UI-thread allocated bytes");
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "settings-sources-refresh-performance.json"),
            JsonSerializer.Serialize(new { View = "Sources", SourceRows = sourceRows, VisualNodes = visualNodes,
                WarmupCount = 20, RefreshCount = 200, Rounds = rounds }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task CheckNoticeScopeAsync(Action<bool, string> check, string screenshots)
    {
        var store = new SettingsStore(Path.Combine(AppContext.BaseDirectory, "verification-layout-notices.json"));
        store.Save(new(SetupCompleted: true, SystemNotifications: true, WeChat: false, Telegram: false, Power: false));
        using var updates = new UpdateCheckService(store);
        var ready = false;
        var window = new SettingsWindow(store, updates, () => new(
                ready ? NotificationAccess.Allowed : NotificationAccess.NeedsRegistration, ready, []),
            () => Task.CompletedTask, () => Task.CompletedTask, () => { }, () => { }, verification: true);
        SettingsPage? noticePage = null;
        try
        {
            window.AppWindow.Resize(new SizeInt32(840, 760));
            window.Activate();
            var page = Tree((DependencyObject)window.Content).OfType<SettingsPage>().Single();
            noticePage = page;
            await SettleAsync(page);
            var notice = Find<InfoBar>(page, "OverviewSystemStatus")!;
            check(IsVisible(notice, page) && notice.IsOpen, "An unready optional system-notification capability is explained on overview");
            await CaptureAsync(window, Path.Combine(screenshots, "overview-system-not-ready.png"));
            Invoke(Find<IconActionButton>(page, "OverviewSetup")!);
            await SettleAsync(page);
            var setupNotice = Find<InfoBar>(page, "SetupStatus")!;
            check(CurrentSection(page) == "setup" && IsVisible(setupNotice, page),
                "The overview initialization action opens the ordered setup flow and its local explanation");
            await CaptureAsync(window, Path.Combine(screenshots, "setup-system-not-ready.png"));
            foreach (var section in Sections.Where(section => section.Id != "overview").Append((Id: "plugins", Title: "PluginManagement")))
            {
                await SelectAsync(page, section.Id);
                check(Find<InfoBar>(page, "OverviewSystemStatus") is null
                    && Find<InfoBar>(page, "SetupStatus") is { Visibility: Visibility.Collapsed },
                    "Unrelated pages do not repeat the system-notification initialization warning: " + section.Id);
            }
            ready = true;
            await SelectAsync(page, "overview");
            notice = Find<InfoBar>(page, "OverviewSystemStatus")!;
            check(notice.Visibility == Visibility.Collapsed && !notice.IsOpen && notice.Title == "" && notice.Message == "",
                "Completed initialization collapses the overview warning and clears its previous text");
        }
        finally
        {
            window.Close();
            var timer = typeof(SettingsWindow).GetField("_refresh", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
                as Microsoft.UI.Dispatching.DispatcherQueueTimer;
            check(timer is { IsRunning: false }, "Closing only the private notice fixture stops its periodic refresh timer");
            if (noticePage is not null)
                check(typeof(SettingsPage).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(noticePage) is true,
                    "Closing the private notice fixture synchronously disposes its SettingsPage");
        }
    }

    public static async Task CheckExistingDisplayDpiAsync(SettingsWindow window, SettingsPage page, Action<bool, string> check)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var root = (FrameworkElement)window.Content;
        var originalPosition = window.AppWindow.Position;
        var originalSize = window.AppWindow.Size;
        var originalDpi = GetDpiForWindow(hwnd);
        var originalScale = root.XamlRoot.RasterizationScale;
        var originalTheme = root.RequestedTheme;
        var originalLanguage = Localization.Language;
        var originalSection = CurrentSection(page);
        var output = Path.Combine(AppContext.BaseDirectory, "settings-existing-display-dpi");
        Directory.CreateDirectory(output);
        var areas = DisplayArea.FindAll();
        // Index the WinRT collection to avoid its IEnumerable projection on this runtime.
        var displays = Enumerable.Range(0, areas.Count).Select(index =>
        {
            var area = areas[index];
            var bounds = area.OuterBounds;
            var rectangle = new WindowRect { Left = bounds.X, Top = bounds.Y, Right = bounds.X + bounds.Width, Bottom = bounds.Y + bounds.Height };
            var monitor = MonitorFromRect(ref rectangle, 0);
            var result = GetScaleFactorForMonitor(monitor, out var percent);
            return new { Area = area, Bounds = bounds, ScalePercent = percent, QuerySucceeded = monitor != 0 && result == 0 };
        }).ToArray();
        var secondary = displays.FirstOrDefault(display => !display.Area.IsPrimary && display.QuerySucceeded && display.ScalePercent == 150);
        var primary = displays.FirstOrDefault(display => display.Area.IsPrimary && display.QuerySucceeded && display.ScalePercent == 100);
        var snapshot = FindDisplayEnvironmentSnapshot();
        var results = new List<object>();
        var completed = false;
        if (secondary is null || primary is null)
        {
            File.WriteAllText(Path.Combine(output, "display-dpi-validation.json"), JsonSerializer.Serialize(new
            {
                Status = "Skipped", Reason = "Existing primary 100% and secondary 150% displays are required.",
                GlobalSettingsChanged = false, EnvironmentSnapshot = snapshot,
                Displays = displays.Select(display => new { display.Bounds, display.ScalePercent, display.QuerySucceeded })
            }, new JsonSerializerOptions { WriteIndented = true }));
            check(true, "SKIP existing-display DPI matrix: the required live 100% and 150% displays are not both available; no global display setting was changed");
            return;
        }
        try
        {
            await MoveToDisplayAsync(primary.Area, 96, 840, 720);
            check(GetDpiForWindow(hwnd) == 96 && Math.Abs(root.XamlRoot.RasterizationScale - 1) < 0.01,
                "The private settings window observes native 100% DPI on the existing primary display");
            foreach (var language in new[] { "zh-CN", "en-US" })
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                    foreach (var width in new[] { 520, 840, 900 })
                    {
                        Localization.Configure(language);
                        root.RequestedTheme = theme;
                        await MoveToDisplayAsync(secondary.Area, 144, width, 720);
                        check(GetClientRect(hwnd, out var client), "The isolated 150% window exposes physical client bounds");
                        var scale = root.XamlRoot.RasterizationScale;
                        check(GetDpiForWindow(hwnd) == 144 && Math.Abs(scale - 1.5) < 0.01
                            && Math.Abs(root.ActualWidth * scale - (client.Right - client.Left)) <= 2
                            && Math.Abs(root.ActualHeight * scale - (client.Bottom - client.Top)) <= 2,
                            $"Native window DPI and XAML logical dimensions agree on the existing 150% display: {language}/{theme}/{width} DIP");
                        foreach (var section in Sections.Append((Id: "plugins", Title: "PluginManagement")))
                        {
                            await SelectAsync(page, section.Id);
                            var context = $"existing-150-percent/{section.Id}/{language}/{theme}/{width}-DIP";
                            var header = Tree(page).OfType<SettingsHeader>().Single();
                            check(header.Title == Localization.Get(section.Title), "DPI migration preserves the routed page title: " + context);
                            CheckBounds(page, header, check, context);
                            CheckIconActions(page, check, context);
                            var title = Tree(header).OfType<TextBlock>().First(text => text.Text == header.Title);
                            check(Math.Abs(title.FontSize - SettingsTokens.PageTitleSize) < 0.001,
                                "Display scaling preserves the configured logical font size: " + context);
                            if (section.Id == "appearance") CheckPreviewBounds(page, check, context);
                            var screenshot = section.Id + "-" + language + "-" + theme + "-" + width + "-DIP-150-percent.png";
                            await CaptureAsync(window, Path.Combine(output, screenshot));
                            results.Add(new
                            {
                                Section = section.Id, Language = language, Theme = theme.ToString(), RequestedWidthDip = width,
                                RequestedHeightDip = 720, NativeDpi = GetDpiForWindow(hwnd), RasterizationScale = scale,
                                PhysicalWidth = window.AppWindow.Size.Width, PhysicalHeight = window.AppWindow.Size.Height,
                                LogicalWidth = root.ActualWidth, LogicalHeight = root.ActualHeight, Screenshot = screenshot
                            });
                        }
                    }
            completed = true;
        }
        finally
        {
            Localization.Configure(originalLanguage);
            root.RequestedTheme = originalTheme;
            window.AppWindow.MoveAndResize(new RectInt32(originalPosition.X, originalPosition.Y, originalSize.Width, originalSize.Height));
            await UntilAsync(() => GetDpiForWindow(hwnd) == originalDpi && Math.Abs(root.XamlRoot.RasterizationScale - originalScale) < 0.01);
            window.AppWindow.MoveAndResize(new RectInt32(originalPosition.X, originalPosition.Y, originalSize.Width, originalSize.Height));
            await SettleAsync(page);
            await SelectAsync(page, originalSection);
            check(window.AppWindow.Position.X == originalPosition.X && window.AppWindow.Position.Y == originalPosition.Y
                && window.AppWindow.Size.Width == originalSize.Width && window.AppWindow.Size.Height == originalSize.Height
                && GetDpiForWindow(hwnd) == originalDpi && Math.Abs(root.XamlRoot.RasterizationScale - originalScale) < 0.01,
                "Existing-display validation restores the private window's original monitor, position, physical size and DPI");
            File.WriteAllText(Path.Combine(output, "display-dpi-validation.json"), JsonSerializer.Serialize(new
            {
                Status = completed ? "Passed" : "Incomplete",
                Scope = "Actual existing-monitor migration; no OS display preference was changed.", GlobalSettingsChanged = false,
                EnvironmentSnapshot = snapshot, OriginalDpi = originalDpi, OriginalRasterizationScale = originalScale,
                RestoredDpi = GetDpiForWindow(hwnd), ValidatedScalePercent = 150,
                RemainingUnvalidatedScalePercent = new[] { 125, 200 }, Results = results
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        async Task MoveToDisplayAsync(DisplayArea display, uint dpi, int widthDip, int heightDip)
        {
            var scale = dpi / 96d;
            var width = (int)Math.Round(widthDip * scale);
            var height = (int)Math.Round(heightDip * scale);
            var work = display.WorkArea;
            check(width <= work.Width && height <= work.Height, "The requested logical validation window fits the target display work area");
            var x = work.X + (work.Width - width) / 2;
            var y = work.Y + (work.Height - height) / 2;
            window.AppWindow.Move(new PointInt32(x, y));
            await UntilAsync(() => GetDpiForWindow(hwnd) == dpi && Math.Abs(root.XamlRoot.RasterizationScale - scale) < 0.01);
            window.AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
            await UntilAsync(() => window.AppWindow.Size.Width == width && window.AppWindow.Size.Height == height);
            await SettleAsync(page);
        }
    }

    private static string? FindDisplayEnvironmentSnapshot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "display-validation-environment.json");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static async Task SelectAsync(SettingsPage page, string section)
    {
        var navigation = Tree(page).OfType<NavigationView>().Single();
        navigation.SelectedItem = navigation.MenuItems.Concat(navigation.FooterMenuItems)
            .OfType<NavigationViewItem>().Single(item => item.Tag is string id && id == section);
        await SettleAsync(page);
        if (CurrentSection(page) != section) throw new InvalidOperationException("Navigation did not select " + section);
    }

    private static async Task SettleAsync(SettingsPage page)
    {
        page.Refresh();
        await Task.Delay(170);
        page.UpdateLayout();
    }

    private static string CurrentSection(SettingsPage page) =>
        AutomationProperties.GetAutomationId((FrameworkElement)Find<ScrollViewer>(page, "SettingsContentScroll")!.Content)
            .Replace("SettingsSection", "", StringComparison.Ordinal);
    private static T? Find<T>(DependencyObject root, string id) where T : FrameworkElement => Tree(root)
        .OfType<T>().SingleOrDefault(element => AutomationProperties.GetAutomationId(element) == id);
    private static Rect Bounds(FrameworkElement element, UIElement relative) =>
        element.TransformToVisual(relative).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    private static bool ContainsHorizontal(Rect outer, Rect inner) => inner.Left >= outer.Left - 2 && inner.Right <= outer.Right + 2;
    private static string? ToolTipText(FrameworkElement element) => ToolTipService.GetToolTip(element) is ToolTip tip
        ? tip.Content?.ToString() : ToolTipService.GetToolTip(element)?.ToString();
    private static bool IsVisible(FrameworkElement element, DependencyObject root)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { Visibility: Visibility.Collapsed }) return false;
            if (ReferenceEquals(current, root)) return true;
        }
        return false;
    }
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static async Task UntilAsync(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("The native settings action did not complete.");
            await Task.Delay(25);
        }
    }
    private static async Task CaptureAsync(Window window, string path)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        ShowWindow(hwnd, 9);
        await Task.Delay(100);
        if (!IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var bounds))
            throw new InvalidOperationException("The target settings window must be visible before native capture.");
        using var bitmap = new System.Drawing.Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            try { if (!PrintWindow(hwnd, dc, 2)) throw new InvalidOperationException("Native settings-window capture failed."); }
            finally { graphics.ReleaseHdc(dc); }
        }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out WindowRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PrintWindow(nint hwnd, nint dc, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint MonitorFromRect(ref WindowRect rectangle, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint hwnd, out WindowRect rectangle);
    [DllImport("shcore.dll")] private static extern int GetScaleFactorForMonitor(nint monitor, out int scalePercent);
}
