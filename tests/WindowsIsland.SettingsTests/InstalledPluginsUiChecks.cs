using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using WindowsIsland.Components;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland;

// 由隔离设置夹具编译；使用其私有管理器，不接触正式来源或插件目录。
internal static class InstalledPluginsUiChecks
{
    public static async Task RunAsync(SettingsPage page, PluginManager manager, string fixtureId, Action<bool, string> check)
    {
        var definition = manager.Snapshot().Single(item => item.Definition.Id == fixtureId).Definition;
        var wasEnabled = manager.Snapshot().Single(item => item.Definition.Id == fixtureId).StatusKey != "Off";
        var originalClipboard = await CaptureClipboardAsync();
        try
        {
            await SelectInstalledAsync(page);
            var originalRow = Row(page, fixtureId);
            check(originalRow.Header.Detail.Contains(definition.Metadata!.Version),
                "Installed plugin rows show the fixture version and metadata");
            await OpenAsync(page, fixtureId);
            var enabled = Enabled(page, fixtureId);
            check(enabled.IsOn == wasEnabled, "Installed details read the existing host enable preference");

            enabled.IsOn = false;
            await UntilAsync(() => enabled.IsEnabled && Status(manager, fixtureId) == "Off");
            check(!enabled.IsOn, "Disabling installed details updates the same plugin manager");
            Back(page);
            await SelectSectionAsync(page, "sources");
            var source = Tree(page).OfType<ToggleSwitch>().Single(item => AutomationProperties.GetAutomationId(item) == definition.ToggleId);
            check(!source.IsOn, "The source switch shares the disabled preference from plugin management");
            source.IsOn = true;
            await UntilAsync(() => source.IsEnabled && Status(manager, fixtureId) != "Off");
            await SelectInstalledAsync(page);
            await OpenAsync(page, fixtureId);
            enabled = Enabled(page, fixtureId);
            check(enabled.IsOn, "Enabling the source is reflected immediately in installed details");

            Invoke(Button(page, "PluginDetail." + fixtureId + ".Id.Copy"));
            await UntilAsync(() => Clipboard.GetContent().Contains(StandardDataFormats.Text));
            check(await Clipboard.GetContent().GetTextAsync() == fixtureId,
                "The installed detail copy action writes the complete plugin identifier");

            var location = definition.Metadata.Location;
            var settings = await manager.GetSettingsAsync(fixtureId);
            var message = settings?.Settings.GetString("message");
            var remove = Button(page, "InstalledPluginRemove." + fixtureId);
            Invoke(remove);
            await UntilAsync(() => OpenDialogs(page).Any());
            var dialog = OpenDialogs(page).Single();
            check(dialog.DefaultButton == ContentDialogButton.Close && dialog.XamlRoot == page.XamlRoot
                && dialog.Content?.ToString() == Localization.Get("PluginRemoveQuestion"),
                "Uninstall confirmation explains retention and defaults to cancel on the active XamlRoot");
            var cancel = ReadyDialogButton(dialog, "CloseButton", dialog.CloseButtonText)
                ?? throw new InvalidOperationException("The visible uninstall cancel button is not ready.");
            Invoke(cancel);
            await UntilAsync(() => !OpenDialogs(page).Any() && remove.IsEnabled);
            check(manager.Snapshot().Any(item => item.Definition.Id == fixtureId && ReferenceEquals(item.Definition.Metadata, definition.Metadata))
                && File.Exists(location) && (await manager.GetSettingsAsync(fixtureId))?.Settings.GetString("message") == message,
                "Canceling uninstall preserves the plugin code, registration and settings");

            Back(page);
            await UntilAsync(() => Tree(page).OfType<SettingsInstalledPlugins>().Any());
            var views = Tree(page).OfType<SelectorBar>().Single();
            check(AutomationProperties.GetAutomationId(views.SelectedItem) == "PluginInstalledView"
                && ReferenceEquals(originalRow, Row(page, fixtureId)),
                "Back restores the installed view and reuses its original plugin row");

            await CheckFailedSourceAsync(page, manager, check);
        }
        finally
        {
            await manager.SetEnabledAsync(fixtureId, wasEnabled);
            page.Refresh();
            Clipboard.SetContent(originalClipboard);
        }
    }

    private static async Task CheckFailedSourceAsync(SettingsPage page, PluginManager manager, Action<bool, string> check)
    {
        // 主夹具的微信工厂只抛异常，不启动真实客户端，用其构造已启用但未连接的结果。
        var id = BuiltInPluginIds.WeChat;
        var wasEnabled = Status(manager, id) != "Off";
        try
        {
            await manager.SetEnabledAsync(id, true);
            page.Refresh();
            await SelectInstalledAsync(page);
            await OpenAsync(page, id);
            var enabled = Enabled(page, id);
            check(Status(manager, id) == "Unavailable" && enabled.IsOn,
                "A failed source remains enabled while its connection result reports failure");
            enabled.IsOn = false;
            await UntilAsync(() => enabled.IsEnabled && Status(manager, id) == "Off");
            check(!enabled.IsOn, "Only an explicit disable changes the failed source to off");
            Back(page);
            await UntilAsync(() => Tree(page).OfType<SettingsInstalledPlugins>().Any());
        }
        finally { await manager.SetEnabledAsync(id, wasEnabled); page.Refresh(); }
    }

    private static async Task<DataPackage> CaptureClipboardAsync()
    {
        var previous = Clipboard.GetContent();
        var copy = new DataPackage();
        foreach (var format in previous.AvailableFormats) copy.SetData(format, await previous.GetDataAsync(format));
        return copy;
    }

    private static string Status(PluginManager manager, string id) => manager.Snapshot().Single(item => item.Definition.Id == id).StatusKey;
    private static SettingsLinkRow Row(SettingsPage page, string id) => Tree(page).OfType<SettingsLinkRow>()
        .Single(item => AutomationProperties.GetAutomationId(item) == "InstalledPlugin." + id);
    private static ToggleSwitch Enabled(SettingsPage page, string id) => Tree(page).OfType<ToggleSwitch>()
        .Single(item => AutomationProperties.GetAutomationId(item) == "InstalledPluginEnabled." + id);
    private static Button Button(DependencyObject root, string id) => Tree(root).OfType<Button>()
        .Single(item => AutomationProperties.GetAutomationId(item) == id);
    private static void Back(SettingsPage page) => Invoke(Button(page, "BackToPluginList"));
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static async Task SelectSectionAsync(SettingsPage page, string section)
    {
        var navigation = Tree(page).OfType<NavigationView>().Single();
        navigation.SelectedItem = navigation.MenuItems.OfType<NavigationViewItem>().Single(item => item.Tag is string tag && tag == section);
        await UntilAsync(() => Tree(page).OfType<FrameworkElement>().Any(item => AutomationProperties.GetAutomationId(item) == "SettingsSection" + section));
    }

    private static async Task SelectInstalledAsync(SettingsPage page)
    {
        await SelectSectionAsync(page, "plugins");
        var views = Tree(page).OfType<SelectorBar>().Single();
        views.SelectedItem = views.Items.Single(item => AutomationProperties.GetAutomationId(item) == "PluginInstalledView");
        await UntilAsync(() => Tree(page).OfType<SettingsInstalledPlugins>().Any());
    }

    private static async Task OpenAsync(SettingsPage page, string id)
    {
        Invoke(Button(Row(page, id), "InstalledPlugin." + id + ".Open"));
        await UntilAsync(() => Tree(page).OfType<ToggleSwitch>().Any(item => AutomationProperties.GetAutomationId(item) == "InstalledPluginEnabled." + id));
    }

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

    private static async Task UntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Installed plugin UI did not reach the expected state.");
            await Task.Delay(25);
        }
    }
}
