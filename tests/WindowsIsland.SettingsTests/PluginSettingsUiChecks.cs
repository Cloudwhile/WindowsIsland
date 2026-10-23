using System.Text.Json;
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
        SettingsPage? page = null;
        PluginManager? manager = null;
        var results = new List<string>();
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            results.Add("PASS " + message);
            File.WriteAllLines(Path.Combine(output, "plugin-settings-ui.log"), results);
        }
        try
        {
            var store = new SettingsStore(Path.Combine(output, "verification-settings.json"));
            store.Save(new(SetupCompleted: true, SystemNotifications: false, WeChat: false, Telegram: false, Power: false));
            Diagnostics.Initialize(Path.Combine(output, "verification-diagnostics"), true);
            var paths = new PluginPaths(Path.Combine(output, "verification-plugins"));
            manager = new PluginManager([], store);
            manager.ConfigureDirectories(paths);
            await manager.StartAsync();
            using var updates = new UpdateCheckService(store);
            using var library = new PluginLibrary(manager, paths);
            page = new SettingsPage(store, updates, () => new(NotificationAccess.Allowed, true, manager.Snapshot()),
                () => Task.CompletedTask, () => Task.CompletedTask, () => { }, () => { }, () => { }, verification: true,
                setSourceEnabled: manager.SetEnabledAsync, pluginLibrary: library,
                rescanPlugins: manager.RescanAsync, pluginSettings: manager.GetSettingsAsync);
            window = new Window { Content = page };
            window.AppWindow.Resize(new SizeInt32(900, 760));
            window.Activate();
            await Task.Delay(200);
            Check(Tree(page).OfType<SettingsSources>().Any(), "Completed setup defaults to sources");
            var manifest = new PluginManifest("wini_plugin_fixture", "Schema fixture", "1.0.0", "WindowsIsland.PluginFixture.dll",
                "WindowsIsland.PluginFixture.SchemaSource", 1, ["notifications", "settings"]);
            store.SetPluginBoolean(manifest.Id, "Host.Enabled", false);
            var code = paths.Code(PluginSourceKind.Manual, manifest.Id);
            Directory.CreateDirectory(code);
            File.Copy(Path.Combine(output, "fixture", manifest.EntryAssembly), Path.Combine(code, manifest.EntryAssembly));
            File.WriteAllText(Path.Combine(code, "manifest.json"), JsonSerializer.Serialize(manifest, PluginManifestReader.Json));
            var refresh = Tree(page).OfType<Button>().Single(e => AutomationProperties.GetAutomationId(e) == "RefreshSettings");
            ((IInvokeProvider)new ButtonAutomationPeer(refresh).GetPattern(PatternInterface.Invoke)).Invoke();
            await Until(() => Tree(page).OfType<SettingsExpander>().Any(e => AutomationProperties.GetAutomationId(e) == "Plugin." + manifest.Id));
            Check(manager.Snapshot().Count == 1, "Manual refresh rescans and displays a new directory plugin");
            var expander = Tree(page).OfType<SettingsExpander>().Single(e => AutomationProperties.GetAutomationId(e) == "Plugin." + manifest.Id);
            expander.IsExpanded = true;
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
                        page.RequestedTheme = theme;
                        window.AppWindow.Resize(new SizeInt32(width, 760));
                        page.Refresh();
                        await Task.Delay(120);
                        page.UpdateLayout();
                        var editor = Tree(page).OfType<PluginSettingsEditor>().Single();
                        Check(text.ActualWidth > 0 && text.ActualWidth <= editor.ActualWidth + 1 && text.Text == "Edited in native WinUI"
                            && !toggle.IsOn && Tree(editor).OfType<SettingsField>().Count() == 2,
                            $"Fields retain values and fit: {language}/{theme}/{width}");
                        Check(Tree(page).OfType<TextBlock>().Any(e => e.Text == Localization.Get("PluginPermissionNotice")),
                            "Permission explanation follows the current language");
                    }
            using var details = new SettingsPluginDetails(manager.Snapshot().Single().Definition, settings: manager.GetSettingsAsync);
            await details.ReadSizeAsync();
            Check(TreeLogical(details).OfType<TextBox>().Single().Text == "Edited in native WinUI"
                && !TreeLogical(details).OfType<ToggleSwitch>().Single().IsOn, "Reopening details reads persisted values");
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
            app.Exit();
        }
    }
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
