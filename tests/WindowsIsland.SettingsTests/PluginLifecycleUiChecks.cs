using System.IO.Compression;
using System.Reflection;
using System.Text;
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

// 全流程仅使用 GUID 私有目录、夹具 DLL 与可控文件选择，不调用网络或真实安装目录。
internal static class PluginLifecycleUiChecks
{
    private const string Id = "wini_plugin_fixture";
    private const string AssemblyName = "WindowsIsland.PluginFixture.dll";
    private const string SavedMessage = "Saved through native plugin settings";
    private const string SavedData = "retained-lifecycle-data";

    public static async Task RunAsync(Action<bool, string> check)
    {
        var output = Path.Combine(AppContext.BaseDirectory, "plugin-lifecycle-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var assembly = Path.Combine(AppContext.BaseDirectory, "fixture", AssemblyName);
        if (!File.Exists(assembly)) throw new FileNotFoundException("The isolated plugin fixture DLL is required.");
        var versionOne = CreatePackage(output, assembly, "1.0.0");
        var versionTwo = CreatePackage(output, assembly, "2.0.0");
        var invalid = Path.Combine(output, "invalid.zip");
        using (var zip = ZipFile.Open(invalid, ZipArchiveMode.Create)) zip.CreateEntry("missing-manifest.txt");
        var store = new SettingsStore(Path.Combine(output, "settings.json"));
        store.Save(new(SetupCompleted: true, SystemNotifications: false, WeChat: false, Telegram: false, Power: false));
        var paths = new PluginPaths(Path.Combine(output, "plugins"));
        var marker = Path.Combine(paths.Data(Id), "retained.txt");
        var manager = new PluginManager([], store);
        // 仅该私有夹具管理器自动接受自己的已知 DLL；生产回调仍由真实条款窗口处理。
        manager.ConfirmEnableAsync = descriptor => Task.FromResult(descriptor.PluginId == Id
            && descriptor.Author == "Isolated fixture" && descriptor.SourceKind == PluginSourceKind.Manual);
        PluginLibrary? library = null;
        UpdateCheckService? updates = null;
        SettingsPage? page = null;
        Window? window = null;
        TaskCompletionSource<string?>? selection = null;
        var pickerCalls = 0;
        Task<string?> PickAsync(CancellationToken token)
        {
            pickerCalls++;
            return (selection ?? throw new InvalidOperationException("The isolated picker was not armed.")).Task.WaitAsync(token);
        }
        try
        {
            manager.ConfigureDirectories(paths);
            await manager.StartAsync();
            library = new PluginLibrary(manager, paths);
            updates = new UpdateCheckService(store, (_, _) => Task.FromResult<GitHubRelease?>(null));
            page = new SettingsPage(store, updates, () => new(NotificationAccess.Allowed, true, manager.Snapshot()),
                () => Task.CompletedTask, () => Task.CompletedTask, () => { }, () => { }, () => { }, verification: true,
                retrySource: manager.RetryAsync, setSourceEnabled: manager.SetEnabledAsync, removePlugin: manager.RemoveAsync,
                pluginLibrary: library, pickPluginFile: PickAsync, rescanPlugins: manager.RescanAsync, pluginSettings: manager.GetSettingsAsync);
            window = new Window { Content = page };
            window.AppWindow.Resize(new SizeInt32(900, 760));
            window.Activate();
            var navigation = Tree(page).OfType<NavigationView>().Single();
            navigation.SelectedItem = navigation.MenuItems.OfType<NavigationViewItem>().Single(item => item.Tag is "plugins");
            await UntilAsync(page, () => Tree(page).OfType<SettingsPluginLibrary>().Any());

            async Task ChooseAsync(string? file)
            {
                selection = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var expected = pickerCalls + 1;
                Invoke(Button(page, "InstallPlugin"));
                await UntilAsync(page, () => pickerCalls == expected);
                check(!Button(page, "InstallPlugin").IsEnabled, "Manual import disables repeated installation while its picker is pending");
                selection.SetResult(file);
            }

            await ChooseAsync(versionOne);
            await UntilAsync(page, () => InstalledVersion(manager) == "1.0.0" && ImportReady(page)
                && HasNotice(page, "PluginInstalledSuccess", InfoBarSeverity.Success));
            check(manager.Snapshot().Count == 1 && InstalledVersion(manager) == "1.0.0",
                "The native manual import UI installs a private version-one plugin without activating it");
            await CheckInstalledOffAsync(manager, store, check, "initial install");
            Directory.CreateDirectory(paths.Data(Id));
            await File.WriteAllTextAsync(marker, SavedData);
            await OpenInstalledAsync(page);
            check(!Tree(page).OfType<PluginSettingsEditor>().Any(), "Disabled installed details contain no executable schema editor");
            await EnableFixtureAsync(manager, page);
            await UntilAsync(page, () => Tree(page).OfType<PluginSettingsEditor>().Any());
            var message = Tree(page).OfType<TextBox>().Single(item => AutomationProperties.GetAutomationId(item) == "PluginSetting." + Id + ".message");
            var enabled = Tree(page).OfType<ToggleSwitch>().Single(item => AutomationProperties.GetAutomationId(item) == "PluginSetting." + Id + ".enabled");
            check(message.IsEnabled && enabled.IsEnabled,
                "The installed plugin exposes editable native string and boolean schema controls");
            message.Text = SavedMessage;
            enabled.IsOn = false;
            // TextChanged 可以在 Text 赋值后的下一次调度才触发；等待生产事件完成实际保存。
            await UntilAsync(page, async () => await SchemaPreservedAsync(manager) && SchemaOnDisk(manager));
            check(await SchemaPreservedAsync(manager) && SchemaOnDisk(manager)
                && manager.Snapshot().Single().Definition.IsEnabled(store.Current),
                "Native schema edits persist before package replacement without changing the host enable preference");
            Back(page);
            await UntilAsync(page, () => Tree(page).OfType<SettingsInstalledPlugins>().Any());

            await ChooseAsync(versionTwo);
            await UntilAsync(page, () => InstalledVersion(manager) == "2.0.0" && ImportReady(page)
                && HasNotice(page, "PluginInstalledSuccess", InfoBarSeverity.Success));
            await CheckInstalledOffAsync(manager, store, check, "higher-version update");
            check(!OpenDialogs(page).Any() && SchemaOnDisk(manager) && await File.ReadAllTextAsync(marker) == SavedData,
                "A higher-version UI import updates without a same-version overwrite dialog and preserves settings and data");
            await EnableFixtureAsync(manager, page);
            check(await SchemaPreservedAsync(manager), "Explicitly enabling the updated isolated worker restores retained schema preferences");

            await ChooseAsync(versionTwo);
            await UntilAsync(page, () => OpenDialogs(page).Any());
            var overwrite = OpenDialogs(page).Single();
            check(overwrite.Title?.ToString() == Localization.Get("PluginAlreadyInstalled")
                && overwrite.DefaultButton == ContentDialogButton.Close,
                "Reimporting the same version presents an overwrite dialog that defaults to cancel");
            var beforeCancel = manager.Snapshot().Single().Definition.Metadata;
            Invoke(DialogButton(overwrite, "CloseButton"));
            await UntilAsync(page, () => !OpenDialogs(page).Any() && ImportReady(page));
            check(ReferenceEquals(beforeCancel, manager.Snapshot().Single().Definition.Metadata)
                && InstalledVersion(manager) == "2.0.0" && await SchemaPreservedAsync(manager)
                && await File.ReadAllTextAsync(marker) == SavedData,
                "Canceling overwrite keeps the current instance, version, schema preferences and data");

            await ChooseAsync(versionTwo);
            await UntilAsync(page, () => OpenDialogs(page).Any());
            Invoke(DialogButton(OpenDialogs(page).Single(), "PrimaryButton"));
            await UntilAsync(page, () => !OpenDialogs(page).Any() && ImportReady(page)
                && HasNotice(page, "PluginInstalledSuccess", InfoBarSeverity.Success));
            check(!ReferenceEquals(beforeCancel, manager.Snapshot().Single().Definition.Metadata)
                && InstalledVersion(manager) == "2.0.0" && SchemaOnDisk(manager)
                && await File.ReadAllTextAsync(marker) == SavedData,
                "Confirming overwrite replaces the package through the UI and retains schema preferences and data");
            await CheckInstalledOffAsync(manager, store, check, "confirmed overwrite");
            await EnableFixtureAsync(manager, page);
            check(await SchemaPreservedAsync(manager), "Confirmed overwrite requires explicit reactivation before exposing runtime settings");

            await OpenInstalledAsync(page);
            var retainedSettings = PluginConsentPolicy.HostSettingsPath(manager.Snapshot().Single().Definition.Metadata!.External!);
            Invoke(Button(page, "InstalledPluginRemove." + Id));
            await UntilAsync(page, () => OpenDialogs(page).Any());
            var remove = OpenDialogs(page).Single();
            check(remove.Content?.ToString() == Localization.Get("PluginRemoveQuestion")
                && remove.DefaultButton == ContentDialogButton.Close,
                "The native uninstall dialog states that plugin data will be retained");
            Invoke(DialogButton(remove, "PrimaryButton"));
            await UntilAsync(page, () => manager.Snapshot().Count == 0
                && HasNotice(page, "PluginRemovedSuccess", InfoBarSeverity.Success));
            var saved = new SettingsStore(retainedSettings);
            check(!Directory.Exists(paths.Code(PluginSourceKind.Manual, Id))
                && await File.ReadAllTextAsync(marker) == SavedData
                && saved.GetPluginString(Id, "message", null) == SavedMessage && !saved.GetPluginBoolean(Id, "enabled", true),
                "Confirmed UI uninstall removes private plugin code and retains its data and schema preferences");

            await ChooseAsync(invalid);
            await UntilAsync(page, () => ImportReady(page) && HasNotice(page, "PluginErrorManifest", InfoBarSeverity.Error));
            check(manager.Snapshot().Count == 0 && await File.ReadAllTextAsync(marker) == SavedData,
                "An invalid ZIP reports a native import failure without restoring code or disturbing retained data");
            await ChooseAsync(versionTwo);
            await UntilAsync(page, () => InstalledVersion(manager) == "2.0.0" && ImportReady(page)
                && HasNotice(page, "PluginInstalledSuccess", InfoBarSeverity.Success));
            await CheckInstalledOffAsync(manager, store, check, "reimport after invalid ZIP");
            check(SchemaOnDisk(manager) && await File.ReadAllTextAsync(marker) == SavedData,
                "A valid UI import succeeds after an invalid ZIP and reuses data retained through uninstall");

            var lifecycle = Path.Combine(paths.Data(Id), "lifecycle.txt");
            var beforeDisabledRemoval = await File.ReadAllTextAsync(lifecycle);
            await OpenInstalledAsync(page);
            check(!Tree(page).OfType<PluginSettingsEditor>().Any(), "Reinstalled but disabled details do not execute a schema worker");
            Invoke(Button(page, "InstalledPluginRemove." + Id));
            await UntilAsync(page, () => OpenDialogs(page).Any());
            Invoke(DialogButton(OpenDialogs(page).Single(), "PrimaryButton"));
            await UntilAsync(page, () => manager.Snapshot().Count == 0 && HasNotice(page, "PluginRemovedSuccess", InfoBarSeverity.Success));
            check(await File.ReadAllTextAsync(lifecycle) == beforeDisabledRemoval && File.Exists(retainedSettings)
                && await File.ReadAllTextAsync(marker) == SavedData,
                "Uninstalling a never-reactivated package executes no constructor or lifecycle hooks and retains data and host preferences");
            await ChooseAsync(versionTwo);
            await UntilAsync(page, () => InstalledVersion(manager) == "2.0.0" && ImportReady(page));
            await CheckInstalledOffAsync(manager, store, check, "reinstall after disabled removal");
            await EnableFixtureAsync(manager, page);
            check(await SchemaPreservedAsync(manager), "Explicitly enabling a reinstalled LPAC worker reads the retained host-owned schema preferences");

            var beforePickerCancel = manager.Snapshot().Single().Definition.Metadata;
            await ChooseAsync(null);
            await UntilAsync(page, () => ImportReady(page));
            check(ReferenceEquals(beforePickerCancel, manager.Snapshot().Single().Definition.Metadata)
                && InstalledVersion(manager) == "2.0.0" && !OpenDialogs(page).Any()
                && !Tree(page).OfType<InfoBar>().Any(item => item.Visibility == Visibility.Visible && item.Severity == InfoBarSeverity.Error),
                "Canceling file selection restores manual import and preserves the installed plugin without an error");
        }
        finally
        {
            selection?.TrySetCanceled();
            page?.Dispose();
            window?.Close();
            await manager.ShutdownAsync();
            library?.Dispose();
            updates?.Dispose();
        }
    }

    private static string CreatePackage(string output, string assembly, string version)
    {
        var file = Path.Combine(output, "fixture-" + version + ".zip");
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(assembly, AssemblyName);
        using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open(), new UTF8Encoding(false));
        writer.Write(JsonSerializer.Serialize(new PluginManifest(Id, "Schema fixture", version, AssemblyName,
            "WindowsIsland.PluginFixture.SchemaSource", PluginContract.Version, ["notifications", "settings"], Author: "Isolated fixture"), PluginManifestReader.Json));
        return file;
    }

    private static string? InstalledVersion(PluginManager manager) => manager.Snapshot().SingleOrDefault()?.Definition.Metadata?.Version;
    private static bool ImportReady(SettingsPage page) => Tree(page).OfType<Button>()
        .Any(item => AutomationProperties.GetAutomationId(item) == "InstallPlugin" && item.IsEnabled);
    private static bool HasNotice(SettingsPage page, string key, InfoBarSeverity severity) => Tree(page).OfType<InfoBar>()
        .Any(item => item.IsOpen && item.Visibility == Visibility.Visible && item.Severity == severity && item.Message == Localization.Get(key));
    private static async Task<bool> SchemaPreservedAsync(PluginManager manager)
    {
        var model = await manager.GetSettingsAsync(Id);
        return model is not null && model.Settings.GetString("message") == SavedMessage && !model.Settings.GetBoolean("enabled", true);
    }
    private static bool SchemaOnDisk(PluginManager manager)
    {
        var external = manager.Snapshot().Single().Definition.Metadata!.External!;
        var saved = new SettingsStore(PluginConsentPolicy.HostSettingsPath(external));
        return saved.GetPluginString(Id, "message", null) == SavedMessage && !saved.GetPluginBoolean(Id, "enabled", true);
    }
    private static async Task CheckInstalledOffAsync(PluginManager manager, SettingsStore store, Action<bool, string> check, string context)
    {
        var item = manager.Snapshot().Single();
        var external = item.Definition.Metadata!.External!;
        var instance = typeof(ExternalPlugin).GetField("_instance", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(ExternalPlugin), "_instance");
        check(item.StatusKey == "Off" && !item.Definition.IsEnabled(store.Current)
            && await manager.GetSettingsAsync(Id) is null && instance.GetValue(external) is null
            && AppDomain.CurrentDomain.GetAssemblies().All(assembly => assembly.GetName().Name != "WindowsIsland.PluginFixture"),
            "Installed package stays off with no proxy, constructor or schema execution in the host: " + context);
    }
    private static async Task EnableFixtureAsync(PluginManager manager, SettingsPage page)
    {
        await manager.SetEnabledAsync(Id, true).WaitAsync(TimeSpan.FromSeconds(90));
        await UntilAsync(page, async () => manager.Snapshot().Single().StatusKey == "Connected" && await manager.GetSettingsAsync(Id) is not null);
    }

    private static async Task OpenInstalledAsync(SettingsPage page)
    {
        Invoke(Button(page, "InstalledPlugin." + Id + ".Open"));
        await UntilAsync(page, () => Tree(page).OfType<SettingsPluginDetails>().Any());
    }

    private static void Back(SettingsPage page) => Invoke(Button(page, "BackToPluginList"));
    private static Button Button(DependencyObject root, string id) => Tree(root).OfType<Button>()
        .Single(item => AutomationProperties.GetAutomationId(item) == id);
    private static Button DialogButton(ContentDialog dialog, string name) => ReadyDialogButton(dialog, name,
        name == "PrimaryButton" ? dialog.PrimaryButtonText : dialog.CloseButtonText)
        ?? throw new InvalidOperationException("The visible plugin confirmation button is not ready: " + name);
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<ContentDialog> OpenDialogs(FrameworkElement page) => VisualTreeHelper.GetOpenPopupsForXamlRoot(page.XamlRoot)
        .Where(popup => popup.Child is not null).SelectMany(popup => Tree(popup.Child)).OfType<ContentDialog>()
        .Where(dialog => dialog.IsLoaded && dialog.Visibility == Visibility.Visible && dialog.ActualWidth > 0 && dialog.ActualHeight > 0
            && ReadyDialogButton(dialog, "CloseButton", dialog.CloseButtonText) is not null
            && (string.IsNullOrEmpty(dialog.PrimaryButtonText) || ReadyDialogButton(dialog, "PrimaryButton", dialog.PrimaryButtonText) is not null));
    private static Button? ReadyDialogButton(ContentDialog dialog, string name, string label) => Tree(dialog).OfType<Button>()
        .FirstOrDefault(button => button.IsLoaded && button.Visibility == Visibility.Visible && button.IsEnabled
            && button.ActualWidth > 0 && button.ActualHeight > 0
            && (button.Name == name || label.Length > 0 && button.Content?.ToString() == label));
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var item in Tree(VisualTreeHelper.GetChild(root, index))) yield return item;
    }
    private static async Task UntilAsync(SettingsPage page, Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (true)
        {
            page.Refresh();
            if (predicate()) return;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The private plugin lifecycle UI did not reach its expected state.");
            await Task.Delay(25);
        }
    }
    private static async Task UntilAsync(SettingsPage page, Func<Task<bool>> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (true)
        {
            page.Refresh();
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException("The native plugin schema edits did not persist before the verification deadline.");
            if (await predicate().WaitAsync(remaining)) return;
            await Task.Delay(25);
        }
    }
}
