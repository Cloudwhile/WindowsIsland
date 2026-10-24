using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WindowsIsland.Components;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland;

// Compiled only by Verify-PluginSettings.ps1 into an isolated verification application.
internal static class PluginSettingsUiChecks
{
    public static async Task RunAsync(App app)
    {
        var output = AppContext.BaseDirectory;
        Window? window = null;
        MainWindow? host = null;
        SettingsPage? page = null;
        PluginManager? manager = null;
        var results = new List<string>();
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            results.Add(message.StartsWith("SKIP ", StringComparison.Ordinal) ? message : "PASS " + message);
            File.WriteAllLines(Path.Combine(output, "plugin-settings-ui.log"), results);
        }
        try
        {
            var store = new SettingsStore(Path.Combine(output, "verification-settings.json"));
            store.Save(new(SetupCompleted: true, SystemNotifications: false, WeChat: false, Telegram: false, Power: false));
            Diagnostics.Initialize(Path.Combine(output, "verification-diagnostics"), true);
            var hostStore = new SettingsStore(Path.Combine(output, "verification-host-settings.json"));
            hostStore.Save(new(SetupCompleted: true, SystemNotifications: false, WeChat: true, Telegram: true, Power: true));
            host = new MainWindow(hostStore, verification: true);
            await host.StartListeningAsync();
            Check(true, "The real island host, tray, timers and isolated source backends run during file dialog checks");
            var paths = new PluginPaths(Path.Combine(output, "verification-plugins"));
            manager = new PluginManager([
                new(BuiltInPluginDefinitions.System, () => throw new InvalidOperationException("Disabled source must not start")),
                new(BuiltInPluginDefinitions.WeChat, () => throw new InvalidOperationException("Disabled source must not start")),
                new(BuiltInPluginDefinitions.Telegram, () => throw new InvalidOperationException("Disabled source must not start")),
                new(BuiltInPluginDefinitions.Power, () => throw new InvalidOperationException("Disabled source must not start"))
            ], store);
            manager.ConfigureDirectories(paths);
            await manager.StartAsync();
            using var updates = new UpdateCheckService(store);
            using var library = new PluginLibrary(manager, paths);
            var catalogEntry = new PluginCatalogEntry("wini_plugin_catalog_fixture", "Catalog fixture", "1.0.0",
                "https://github.com/example/plugins/releases/download/v1.0.0/plugin.zip", new string('0', 64),
                "0.0.0", PluginContract.Version, ["notifications", "network"], "Catalog fixture details", "https://github.com/example/plugins", "example");
            Directory.CreateDirectory(paths.Root);
            var catalogState = new PluginCatalogState(PluginCatalogClient.DefaultBaseUrl,
                [new(new("example", "Example developer", catalogEntry.RepositoryUrl,
                    PluginCatalogClient.DefaultBaseUrl + "api/sources/example/plugins"), [catalogEntry]),
                 new(new(PluginCatalogClient.OfficialDeveloperId, "WINI", catalogEntry.RepositoryUrl,
                    PluginCatalogClient.DefaultBaseUrl + "api/sources/winiofficial/plugins"),
                    [catalogEntry with { Id = "wini_plugin_official_fixture", DeveloperId = PluginCatalogClient.OfficialDeveloperId }])]);
            File.WriteAllText(Path.Combine(paths.Root, "sources.json"), JsonSerializer.Serialize(catalogState, PluginManifestReader.Json));
            await library.Catalog.LoadAsync();
            window = new SettingsWindow(store, updates, () => new(NotificationAccess.Allowed, true, manager.Snapshot()),
                () => Task.CompletedTask, () => Task.CompletedTask, () => { }, () => { }, verification: true,
                setSourceEnabled: manager.SetEnabledAsync, pluginLibrary: library,
                rescanPlugins: manager.RescanAsync, pluginSettings: manager.GetSettingsAsync);
            var root = (FrameworkElement)window.Content;
            page = Tree(root).OfType<SettingsPage>().Single();
            window.AppWindow.Resize(new SizeInt32(900, 760));
            window.AppWindow.Show();
            window.Activate();
            await Task.Delay(200);
            Check(Tree(page).OfType<SettingsOverview>().Any(), "Completed setup defaults to overview");
            var only = Environment.GetCommandLineArgs().FirstOrDefault(argument => argument.StartsWith("--verify-ui-check=", StringComparison.Ordinal))?[18..];
            if (only is not null and not "All")
            {
                Check(true, "Focused verification scope: " + only);
                switch (only)
                {
                    case "Layout": await SettingsLayoutUiChecks.RunAsync((SettingsWindow)window, page, store, Check); break;
                    case "HostPreview": await HostPreviewUiChecks.RunAsync(host, hostStore, Check); break;
                    case "SourceState": await SourceStateUiChecks.RunAsync(Check); break;
                    default: throw new InvalidOperationException("Unknown private verification scope.");
                }
                File.WriteAllText(Path.Combine(output, "plugin-settings-ui.success"), results.Count.ToString());
                return;
            }
            await MaintenanceUiChecks.RunAsync((SettingsWindow)window, page, store, Check);
            await SetupFlowUiChecks.RunAsync(Check);
            await SourceStateUiChecks.RunAsync(Check);
            await ListSeparatorsUiChecks.RunAsync(Check);
            await OverviewUiChecks.RunAsync(Check);
            await MotionUiChecks.RunAsync(Check);
            await NavigationContextUiChecks.RunAsync(Check);
            await PluginLifecycleUiChecks.RunAsync(Check);
            window.Activate();
            await SettingsLayoutUiChecks.RunAsync((SettingsWindow)window, page, store, Check);
            await HostPreviewUiChecks.RunAsync(host, hostStore, Check);
            window.Activate();
            var initialNavigation = Tree(page).OfType<NavigationView>().Single();
            initialNavigation.SelectedItem = initialNavigation.MenuItems.OfType<NavigationViewItem>().Single(item => item.Tag is "sources");
            await Until(() => Tree(page).OfType<SettingsSources>().Any());
            foreach (var id in new[] { BuiltInPluginIds.System, BuiltInPluginIds.WeChat, BuiltInPluginIds.Telegram, BuiltInPluginIds.Power })
            {
                var builtInRow = Tree(page).OfType<SettingsLinkRow>().Single(e => AutomationProperties.GetAutomationId(e) == "Plugin." + id);
                Check(Tree(builtInRow).OfType<ToggleSwitch>().Single().Visibility == Visibility.Visible,
                    "Built-in source is visible and retains its independent switch: " + id);
            }
            var systemRow = Tree(page).OfType<SettingsLinkRow>().Single(e => AutomationProperties.GetAutomationId(e) == "Plugin." + BuiltInPluginIds.System);
            Invoke(Tree(systemRow).OfType<Button>().Single(e => AutomationProperties.GetAutomationId(e) == "Plugin." + BuiltInPluginIds.System + ".Open"));
            await Until(() => Tree(page).OfType<SettingsPluginDetails>().Any());
            Check(Tree(page).OfType<TextBlock>().Any(e => AutomationProperties.GetAutomationId(e) == "PluginDetail." + BuiltInPluginIds.System + ".Author"
                && e.Text == "winiofficial"), "System notification details show winiofficial as the official author");
            Invoke(Tree(page).OfType<Button>().Single(e => AutomationProperties.GetAutomationId(e) == "BackToPluginList"));
            await Until(() => Tree(page).OfType<SettingsSources>().Any());
            var manifest = new PluginManifest("wini_plugin_fixture", "Schema fixture", "1.0.0", "WindowsIsland.PluginFixture.dll",
                "WindowsIsland.PluginFixture.SchemaSource", 1, ["notifications", "settings"]);
            store.SetPluginBoolean(manifest.Id, "Host.Enabled", false);
            var code = paths.Code(PluginSourceKind.Manual, manifest.Id);
            Directory.CreateDirectory(code);
            File.Copy(Path.Combine(output, "fixture", manifest.EntryAssembly), Path.Combine(code, manifest.EntryAssembly));
            File.WriteAllText(Path.Combine(code, "manifest.json"), JsonSerializer.Serialize(manifest, PluginManifestReader.Json));
            var refresh = Tree(page).OfType<Button>().Single(e => AutomationProperties.GetAutomationId(e) == "RefreshSettings");
            ((IInvokeProvider)new ButtonAutomationPeer(refresh).GetPattern(PatternInterface.Invoke)).Invoke();
            await Until(() => Tree(page).OfType<SettingsLinkRow>().Any(e => AutomationProperties.GetAutomationId(e) == "Plugin." + manifest.Id));
            Check(manager.Snapshot().Count == 5, "Manual refresh preserves all four built-ins and displays a new directory plugin");
            var sourceRow = Tree(page).OfType<SettingsLinkRow>().Single(e => AutomationProperties.GetAutomationId(e) == "Plugin." + manifest.Id);
            var open = Tree(sourceRow).OfType<Button>().Single(e => AutomationProperties.GetAutomationId(e) == "Plugin." + manifest.Id + ".Open");
            ((IInvokeProvider)new ButtonAutomationPeer(open).GetPattern(PatternInterface.Invoke)).Invoke();
            await Until(() => Tree(page).OfType<PluginSettingsEditor>().Any());
            var text = Tree(page).OfType<TextBox>().Single(e => AutomationProperties.GetAutomationId(e) == "PluginSetting." + manifest.Id + ".message");
            var toggle = Tree(page).OfType<ToggleSwitch>().Single(e => AutomationProperties.GetAutomationId(e) == "PluginSetting." + manifest.Id + ".enabled");
            Check(text.Text == "default message" && toggle.IsOn && text.IsEnabled && toggle.IsEnabled,
                "Disabled plugin renders editable string/bool controls with plugin defaults");
            text.Text = "Edited in native WinUI";
            toggle.IsOn = false;
            await Task.Delay(80);
            var model = (await manager.GetSettingsAsync(manifest.Id))!;
            Check(model.Settings.GetString("message") == text.Text && !model.Settings.GetBoolean("enabled", true),
                "Native text/toggle events immediately persist both settings");
            var disk = new SettingsStore(Path.Combine(paths.Data(manifest.Id), "settings.json"));
            Check(disk.GetPluginString(manifest.Id, "message", null) == text.Text && !disk.GetPluginBoolean(manifest.Id, "enabled", true),
                "UI edits survive disk reload");
            await manager.SetEnabledAsync(manifest.Id, true);
            page.Refresh();
            Check(File.ReadAllText(Path.Combine(paths.Data(manifest.Id), "lifecycle.txt")).Contains("Settings:Edited in native WinUI:False"),
                "Enabled plugin sees settings saved while disabled");
            foreach (var language in new[] { "zh-CN", "en-US" })
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                    foreach (var width in new[] { 900, 520 })
                    {
                        Localization.Configure(language);
                        root.RequestedTheme = theme;
                        window.AppWindow.Resize(new SizeInt32(width, 760));
                        page.Refresh();
                        await Task.Delay(120);
                        page.UpdateLayout();
                        var captionColor = window.AppWindow.TitleBar.ButtonForegroundColor;
                        Check(captionColor is { } color && (theme == ElementTheme.Light ? color.R < 80 : color.R > 175),
                            "Window caption buttons follow the current theme");
                        var editor = Tree(page).OfType<PluginSettingsEditor>().Single();
                        Check(text.ActualWidth > 0 && text.ActualWidth <= editor.ActualWidth + 1 && text.Text == "Edited in native WinUI"
                            && !toggle.IsOn && Tree(editor).OfType<SettingsField>().Count() == 2,
                            $"Fields retain values and fit: {language}/{theme}/{width}");
                        Check(Tree(page).OfType<TextBlock>().Any(e => e.Text == Localization.Get("PluginPermissionNotice")),
                            "Permission explanation follows the current language");
                        var header = Tree(page).OfType<SettingsHeader>().Single();
                        var backAction = Tree(header).OfType<Button>().Single(e => AutomationProperties.GetAutomationId(e) == "BackToPluginList");
                        Check(backAction.ActualWidth > 0 && backAction.ActualHeight > 0
                            && header.ActualWidth <= page.ActualWidth + 1, "Back action remains reachable and the header fits");
                        if (width == 520 && language == "zh-CN" && theme == ElementTheme.Light)
                            await CaptureAsync(window, Path.Combine(output, "plugin-detail.png"));
                    }
            using var details = new SettingsPluginDetails(manager.Snapshot().Single(p => p.Definition.Id == manifest.Id).Definition, settings: manager.GetSettingsAsync);
            await details.ReadSizeAsync();
            Check(TreeLogical(details).OfType<TextBox>().Single().Text == "Edited in native WinUI"
                && !TreeLogical(details).OfType<ToggleSwitch>().Single().IsOn, "Reopening details reads persisted values");
            var back = Tree(page).OfType<Button>().Single(e => AutomationProperties.GetAutomationId(e) == "BackToPluginList");
            Check(back.Visibility == Visibility.Visible && ToolTipService.GetToolTip(back)?.ToString() == Localization.Get("ActionBack"),
                "Plugin details expose a localized top-left back action");
            ((IInvokeProvider)new ButtonAutomationPeer(back).GetPattern(PatternInterface.Invoke)).Invoke();
            await Until(() => Tree(page).OfType<SettingsSources>().Any());
            Check(!Tree(page).OfType<SettingsPluginDetails>().Any() && !Tree(page).OfType<SettingsExpander>().Any(e => AutomationProperties.GetAutomationId(e) == "Plugin." + manifest.Id),
                "Back restores the source list without inline plugin expanders");
            await InstalledPluginsUiChecks.RunAsync(page, manager, manifest.Id, Check);
            var navigation = Tree(page).OfType<NavigationView>().Single();
            navigation.SelectedItem = navigation.MenuItems.OfType<NavigationViewItem>().Single(item => item.Tag is "plugins");
            await Until(() => Tree(page).OfType<SelectorBar>().Any());
            var views = Tree(page).OfType<SelectorBar>().Single();
            Check(Tree(page).OfType<SettingsInstalledPlugins>().Any(), "Plugin management defaults to installed plugins");
            views.SelectedItem = views.Items.Single(item => AutomationProperties.GetAutomationId(item) == "PluginDiscoverView");
            await Until(() => Tree(page).OfType<SettingsOfficialPlugins>().Any());
            var advanced = Tree(page).OfType<SettingsExpander>().Single(e => AutomationProperties.GetAutomationId(e) == "PluginAdvancedSettings");
            advanced.IsExpanded = true;
            await Until(() => Tree(page).OfType<TextBox>().Any(e => AutomationProperties.GetAutomationId(e) == "PluginGitHubProxy"));
            Check(!Tree(page).OfType<TextBox>().Any(e => AutomationProperties.GetAutomationId(e) == "PluginSourceAddress")
                && !Tree(page).OfType<Button>().Any(e => AutomationProperties.GetAutomationId(e) == "SavePluginSourceAddress")
                && !Tree(page).OfType<TextBlock>().Any(e => e.Text.Contains(PluginCatalogClient.DefaultBaseUrl)),
                "C1: the official source address is neither displayed nor editable");
            Check(Tree(page).OfType<TextBlock>().Any(e => e.Text == "winiofficial")
                && !Tree(page).OfType<TextBlock>().Any(e => e.Text == "WINI"), "The official catalog group displays winiofficial even with a legacy developer name");
            await NativeFileDialogUiChecks.RunAsync(window, page, Check);
            var proxy = Tree(page).OfType<TextBox>().Single(e => AutomationProperties.GetAutomationId(e) == "PluginGitHubProxy");
            var save = Tree(page).OfType<Button>().Single(e => AutomationProperties.GetAutomationId(e) == "SavePluginProxy");
            proxy.Text = "https://proxy.example/";
            ((IInvokeProvider)new ButtonAutomationPeer(save).GetPattern(PatternInterface.Invoke)).Invoke();
            await Until(() => library.Catalog.State.GitHubProxy == "https://proxy.example/" && save.IsEnabled);
            proxy.Text = "";
            ((IInvokeProvider)new ButtonAutomationPeer(save).GetPattern(PatternInterface.Invoke)).Invoke();
            await Until(() => library.Catalog.State.GitHubProxy == "" && save.IsEnabled);
            Check(library.Catalog.State.BaseUrl == PluginCatalogClient.DefaultBaseUrl, "Saving and clearing a GitHub proxy preserve the configured catalog and restore direct downloads");
            await CaptureAsync(window, Path.Combine(output, "plugin-catalog.png"));
            root.RequestedTheme = ElementTheme.Light;
            Localization.Configure("zh-CN");
            await Task.Delay(200);
            await CaptureAsync(window, Path.Combine(output, "plugin-catalog-light.png"));
            var catalogRow = Tree(page).OfType<SettingsLinkRow>().Single(e => AutomationProperties.GetAutomationId(e) == "OfficialPlugin." + catalogEntry.Id);
            Invoke(Tree(catalogRow).OfType<Button>().Single(e => AutomationProperties.GetAutomationId(e) == "OfficialPlugin." + catalogEntry.Id + ".Open"));
            await Until(() => Tree(page).OfType<TextBlock>().Any(e => e.Text == catalogEntry.Description));
            Check(!Tree(page).OfType<SettingsOfficialPlugins>().Any() && back.Visibility == Visibility.Visible,
                "Official catalog entries open a separate detail page with a back action");
            var install = Tree(page).OfType<Button>().Single(e => AutomationProperties.GetAutomationId(e) == "OfficialInstall." + catalogEntry.Id);
            Invoke(install);
            await Until(() => OpenDialogs(page).Any());
            var dialog = OpenDialogs(page).Single();
            Check(dialog.XamlRoot == page.XamlRoot && Tree(dialog).OfType<CheckBox>().Any(e => e.Tag is "network"),
                "Official detail installation presents permissions on the active page");
            dialog.Hide();
            await Until(() => install.IsEnabled);
            await CaptureAsync(window, Path.Combine(output, "official-plugin-detail.png"));
            root.RequestedTheme = ElementTheme.Dark;
            await Task.Delay(200);
            await CaptureAsync(window, Path.Combine(output, "official-plugin-detail-dark.png"));
            Invoke(back);
            await Until(() => Tree(page).OfType<SettingsOfficialPlugins>().Any());
            Check(Tree(page).OfType<SettingsLinkRow>().Any(e => AutomationProperties.GetAutomationId(e) == "OfficialPlugin." + catalogEntry.Id)
                && (await manager.GetSettingsAsync(manifest.Id))!.Settings.GetString("message") == "Edited in native WinUI",
                "Returning restores the official list and preserves plugin settings");
            using (var reloadedCatalog = new PluginCatalogService(paths))
            {
                await reloadedCatalog.LoadAsync();
                Check(reloadedCatalog.State.BaseUrl == PluginCatalogClient.DefaultBaseUrl && reloadedCatalog.State.Sources.Length == 2,
                    "C1/C4: the fixed official source and cache survive a reload");
            }
            navigation.SelectedItem = navigation.MenuItems.OfType<NavigationViewItem>().Single(item => item.Tag is "plugins");
            await Until(() => Tree(page).OfType<SettingsPluginLibrary>().Any());
            await NativeFileDialogUiChecks.CheckWindowCloseAsync(window, page, Check);
            window = null;
            page = null;
            File.WriteAllText(Path.Combine(output, "plugin-settings-ui.success"), results.Count.ToString());
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "plugin-settings-ui.error"), error.ToString());
            Environment.ExitCode = 1;
        }
        finally
        {
            page?.Dispose();
            if (manager is not null) await manager.ShutdownAsync();
            Diagnostics.Shutdown();
            window?.Close();
            host?.ExitApplication();
            app.Exit();
        }
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<ContentDialog> OpenDialogs(FrameworkElement page) => VisualTreeHelper.GetOpenPopupsForXamlRoot(page.XamlRoot)
        .Where(popup => popup.Child is not null).SelectMany(popup => Tree(popup.Child)).OfType<ContentDialog>()
        .Where(dialog => dialog.IsLoaded && dialog.ActualWidth > 0 && dialog.ActualHeight > 0
            && Tree(dialog).OfType<Button>().Any(button => button.Name == "CloseButton" && button.IsLoaded && button.ActualWidth > 0));
    private static async Task CaptureAsync(Window window, string path)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        ShowWindow(hwnd, 9);
        await Task.Delay(250);
        if (!IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var bounds))
            throw new InvalidOperationException("The verification window must be visible before capture.");
        using var bitmap = new System.Drawing.Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            try { if (!PrintWindow(hwnd, dc, 2)) throw new InvalidOperationException("Native window capture failed."); }
            finally { graphics.ReleaseHdc(dc); }
        }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint hwnd, int command);
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out WindowRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PrintWindow(nint hwnd, nint dc, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Tree(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
    private static IEnumerable<DependencyObject> TreeLogical(DependencyObject root)
    {
        yield return root;
        if (root is Panel panel)
            foreach (var child in panel.Children)
                foreach (var item in TreeLogical(child)) yield return item;
    }
    private static async Task Until(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Native settings control did not appear.");
            await Task.Delay(50);
        }
    }
}
