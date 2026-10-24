using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WindowsIsland.Components;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland;

// 私有诊断记录和本地官方目录缓存，不下载插件、不改正式诊断。
internal static class ListSeparatorsUiChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var output = Path.Combine(AppContext.BaseDirectory, "list-separators-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        using var records = new DiagnosticStore(Path.Combine(output, "diagnostics"), true);
        records.Record(DiagnosticArea.Application, "UserInterface", new InvalidOperationException());
        records.Record(DiagnosticArea.Application, "Start", new InvalidOperationException());
        records.Record(DiagnosticArea.Application, "Retry", new InvalidOperationException());
        var entries = records.Snapshot();
        var diagnostics = new SettingsDiagnosticEntries();
        diagnostics.Refresh([entries[0], entries[1]]);
        var settings = new SettingsStore(Path.Combine(output, "settings.json"));
        settings.Save(new(SystemNotifications: false, WeChat: false, Telegram: false, Power: false));
        var paths = new PluginPaths(Path.Combine(output, "plugins"));
        var manager = new PluginManager([], settings);
        using var library = new PluginLibrary(manager, paths);
        var all = Enumerable.Range(1, 3).Select(index => new PluginCatalogEntry(
            "wini_plugin_separator_" + index, "Separator fixture " + index, "1.0.0",
            "https://github.com/example/plugins/releases/download/v1.0.0/fixture-" + index + ".zip", new string('0', 64),
            "0.0.0", PluginContract.Version, [], "Local separator fixture", "https://github.com/example/plugins", "separatorfixture")).ToArray();
        await SetCatalogAsync(library, paths, [all[0], all[1]]);
        using var official = new SettingsOfficialPlugins(library, _ => { });
        var lists = new StackPanel();
        lists.Children.Add(diagnostics);
        lists.Children.Add(official);
        var content = SettingsSection.Page(lists);
        var window = new Window { Content = content };
        try
        {
            window.Activate();
            await UntilAsync(() => diagnostics.IsLoaded && official.IsLoaded);
            await Task.Delay(50);
            CheckHeaders(diagnostics.Children.OfType<SettingsExpander>().Select(row => row.Header).ToArray(), 2, check,
                "Loaded diagnostic entries keep the first separator and hide only the final one");
            CheckHeaders(OfficialRows(official), 2, check,
                "Loaded official entries treat link and notice as one row instead of hiding every separator");

            diagnostics.Refresh(entries);
            CheckHeaders(diagnostics.Children.OfType<SettingsExpander>().Select(row => row.Header).ToArray(), 3, check,
                "Appending a diagnostic record restores the former last separator");
            diagnostics.Refresh([entries[2], entries[0], entries[1]]);
            var reordered = diagnostics.Children.OfType<SettingsExpander>().ToArray();
            check(AutomationProperties.GetAutomationId(reordered[0]) == "DiagnosticEntry." + entries[2].Id,
                "The diagnostic fixture exercises an actual row reorder");
            CheckHeaders(reordered.Select(row => row.Header).ToArray(), 3, check,
                "Reordering diagnostic entries updates separator ownership by final row position");
            diagnostics.Refresh([entries[2], entries[0]]);
            CheckHeaders(diagnostics.Children.OfType<SettingsExpander>().Select(row => row.Header).ToArray(), 2, check,
                "Removing the final diagnostic record hides the newly final separator");
            diagnostics.Refresh([entries[0]]);
            CheckHeaders(diagnostics.Children.OfType<SettingsExpander>().Select(row => row.Header).ToArray(), 1, check,
                "Removing the earlier diagnostic row leaves the retained single row without a separator");

            await SetCatalogAsync(library, paths, all);
            official.Refresh();
            CheckHeaders(OfficialRows(official), 3, check,
                "Appending a local official entry restores intermediate row separators");
            await SetCatalogAsync(library, paths, [all[2], all[0], all[1]]);
            official.Refresh();
            var catalogRows = OfficialRows(official);
            check(catalogRows[0].Title == all[2].Name, "The official fixture exercises a changed catalog order");
            CheckHeaders(catalogRows, 3, check,
                "Rebuilding a reordered official group hides only its new final row");
            await SetCatalogAsync(library, paths, [all[2], all[0]]);
            official.Refresh();
            CheckHeaders(OfficialRows(official), 2, check,
                "Removing an official entry updates the group separator boundary");

            diagnostics.Refresh([entries[2], entries[0], entries[1]]);
            window.Content = null;
            window.Content = content;
            await Task.Delay(50);
            CheckHeaders(diagnostics.Children.OfType<SettingsExpander>().Select(row => row.Header).ToArray(), 3, check,
                "Page reload does not recurse into each diagnostic logical row");
            CheckHeaders(OfficialRows(official), 2, check,
                "Page reload preserves official group separators across attached notices");
            diagnostics.Refresh([]);
            await SetCatalogAsync(library, paths, []);
            official.Refresh();
            check(diagnostics.Children.Count == 0 && OfficialRows(official).Length == 0,
                "Empty diagnostic and official lists leave no stale separator rows");
        }
        finally { window.Close(); await manager.ShutdownAsync(); }
    }

    private static async Task SetCatalogAsync(PluginLibrary library, PluginPaths paths, PluginCatalogEntry[] entries)
    {
        Directory.CreateDirectory(paths.Root);
        var source = new PluginSource("separatorfixture", "Separator fixture", "https://github.com/example/plugins",
            PluginCatalogClient.DefaultBaseUrl + "api/sources/separatorfixture/plugins");
        var state = new PluginCatalogState(PluginCatalogClient.DefaultBaseUrl, [new(source, entries)]);
        await File.WriteAllTextAsync(Path.Combine(paths.Root, "sources.json"), JsonSerializer.Serialize(state, PluginManifestReader.Json));
        await library.Catalog.LoadAsync();
    }

    private static SettingsRow[] OfficialRows(SettingsOfficialPlugins official) => Tree(official).OfType<SettingsLinkRow>()
        .Where(row => AutomationProperties.GetAutomationId(row).StartsWith("OfficialPlugin.", StringComparison.Ordinal))
        .Select(row => row.Header).ToArray();

    private static void CheckHeaders(SettingsRow[] rows, int expected, Action<bool, string> check, string message) =>
        check(rows.Length == expected && rows.Select((row, index) => HasSeparator(row) == (index < expected - 1)).All(value => value), message);

    private static bool HasSeparator(SettingsRow row) => row.Children.OfType<Border>().Single().Visibility == Visibility.Visible;

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
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The private separator fixture did not load.");
            await Task.Delay(25);
        }
    }
}
