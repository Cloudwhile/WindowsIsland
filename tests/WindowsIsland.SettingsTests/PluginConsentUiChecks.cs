using System.Reflection;
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

internal static class PluginConsentUiChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var descriptor = new PluginConsentDescriptor("wini_plugin_consent_fixture", "Consent fixture", "1.2.3",
            "fixture_studio", "Fixture studio", "Fixture author", PluginSourceKind.Manual,
            new string('a', 64), new string('b', 64),
            new PluginTerms("revision-1", "First terms line\n" + new string('t', 8192) + "\nLast terms line"),
            new PluginNetworkDisclosure(["https://api.example.com"], "Send this plugin's fixture data", ["Fixture text"], "开发者提供的联网须知\n完整第二行"),
            [PluginPermissions.Notifications, PluginPermissions.Network]);
        var layout = new Grid();
        var window = new Window { Content = layout };
        ContentDialog? dialog = null;
        try
        {
            window.AppWindow.Resize(new SizeInt32(520, 600));
            window.Activate();
            await UntilAsync(() => layout.IsLoaded && layout.XamlRoot is not null);

            dialog = PluginConsentDialog.CreateEnable(descriptor);
            Prepare(dialog, layout.XamlRoot);
            var enabled = dialog.ShowAsync();
            await UntilAsync(() => ReadyButton(dialog, "CloseButton") is not null);
            var terms = Find<CheckBox>(dialog, "PluginConsentAcceptTerms");
            var network = Find<CheckBox>(dialog, "PluginConsentAcceptNetwork");
            check(dialog.DefaultButton == ContentDialogButton.Close && !dialog.IsPrimaryButtonEnabled
                && ReadyButton(dialog, "PrimaryButton") is { IsEnabled: false },
                "Native enable consent defaults to cancellation and cannot accept unread terms or network disclosure");
            check(NormalizeLines(Find<TextBox>(dialog, "PluginConsentTermsText").Text) == NormalizeLines(descriptor.Terms!.Text),
                "The native terms viewer contains the complete immutable plain text without truncation");
            check(NormalizeLines(Find<TextBox>(dialog, "PluginConsentNetworkText").Text) == NormalizeLines(descriptor.Network!.Notice!),
                "Enable-time network notice displays the complete developer-provided text");
            terms.IsChecked = true;
            check(!dialog.IsPrimaryButtonEnabled, "Accepting terms alone cannot approve a plugin with network disclosure");
            network.IsChecked = true;
            await UntilAsync(() => ReadyButton(dialog, "PrimaryButton") is { IsEnabled: true });
            check(dialog.IsPrimaryButtonEnabled && ReadyButton(dialog, "PrimaryButton") is { IsEnabled: true },
                "The native enable action becomes available only after both explicit confirmations");
            terms.IsChecked = false;
            check(!dialog.IsPrimaryButtonEnabled, "Withdrawing terms acceptance disables the enable action again");
            Invoke(ReadyButton(dialog, "CloseButton")!);
            check(await enabled != ContentDialogResult.Primary, "Canceling the native enable dialog does not approve the plugin");
            dialog = null;

            var entry = new PluginCatalogEntry(descriptor.PluginId, descriptor.Name, descriptor.Version,
                "https://github.com/example/plugins/releases/download/v1.2.3/fixture.zip", new string('c', 64), "0.1.0", 1,
                ["notifications", "network", "process", "filesystem", "native", "automation"], "Fixture", "https://github.com/example/plugins", "winiofficial");
            dialog = PluginConsentDialog.CreateInstall(entry);
            Prepare(dialog, layout.XamlRoot);
            var installed = dialog.ShowAsync();
            await UntilAsync(() => ReadyButton(dialog, "CloseButton") is not null);
            var unavailable = Find<TextBlock>(dialog, "PluginInstallUnavailable").Text;
            check(Tree(dialog).OfType<TextBlock>().Any(text => text.Text == Localization.Get("PluginDefaultSourceNotice")),
                "Default-source installation displays the stability and security review notice");
            check(!Tree(dialog).OfType<CheckBox>().Any() && new[] { "process", "filesystem", "native", "automation" }.All(unavailable.Contains),
                "Official-author installation is a declaration review with unavailable capabilities and no permission-grant checkboxes");
            Invoke(ReadyButton(dialog, "CloseButton")!);
            await installed;
            dialog = null;

            await CheckWindowBoundaryAsync(descriptor, check);
        }
        finally { dialog?.Hide(); window.Close(); }
    }

    private static async Task CheckWindowBoundaryAsync(PluginConsentDescriptor descriptor, Action<bool, string> check)
    {
        var output = Path.Combine(AppContext.BaseDirectory, "plugin-consent-ui", Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(output, "settings.json"));
        store.Save(new(SetupCompleted: true, SystemNotifications: false, WeChat: false, Telegram: false, Power: false, Animations: false));
        MainWindow? host = null;
        try
        {
            host = new MainWindow(store, verification: true);
            var manager = Private<PluginManager>(host, "_plugins");
            var enable = manager.ConfirmEnableAsync!;
            check(!await enable(descriptor)
                && Private<SettingsWindow?>(host, "_settingsWindow") is null,
                "Background enable consent is denied without opening Settings");
            host.OpenSettings();
            var settings = Private<SettingsWindow>(host, "_settingsWindow");
            settings.AppWindow.Hide();
            check(!await enable(descriptor),
                "An existing hidden Settings window cannot approve plugin enablement");
            settings.Activate();
            try
            {
                await NativeSettingsInput.EnsureForegroundAsync(settings);
            }
            catch (NativeSettingsInput.UnavailableException error)
            {
                check(true, "SKIP foreground-only consent lifecycle checks: " + error.Message);
                return;
            }
            var root = (FrameworkElement)settings.Content;
            var pendingEnable = enable(descriptor);
            await UntilAsync(() => OpenDialogs(root).Any());
            var consent = OpenDialogs(root).Single();
            Invoke(ReadyButton(consent, "CloseButton")!);
            check(!await pendingEnable, "Canceling the current Settings consent returns false to the host callback");
            await UntilAsync(() => !OpenDialogs(root).Any());

            var closingEnable = enable(descriptor);
            await UntilAsync(() => OpenDialogs(root).Any());
            settings.Close();
            check(!await closingEnable && Private<SettingsWindow?>(host, "_settingsWindow") is null,
                "Closing Settings rejects pending enable consent and releases the owning window");
            check(!await enable(descriptor),
                "Late approval callbacks cannot reopen or reuse a closed Settings window");
        }
        finally
        {
            host?.ExitApplication();
        }
    }

    private static string NormalizeLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static void Prepare(ContentDialog dialog, XamlRoot root)
    {
        dialog.XamlRoot = root;
        if (dialog.Content is ScrollViewer scroll) scroll.MaxHeight = Math.Max(80, Math.Min(440, root.Size.Height - 200));
    }

    private static T Private<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static T Find<T>(DependencyObject root, string id) where T : FrameworkElement => Tree(root).OfType<T>()
        .Single(element => AutomationProperties.GetAutomationId(element) == id);
    private static Button? ReadyButton(ContentDialog dialog, string name) => Tree(dialog).OfType<Button>()
        .FirstOrDefault(button => button.Name == name && button.IsLoaded && button.ActualWidth > 0 && button.ActualHeight > 0);
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<ContentDialog> OpenDialogs(FrameworkElement root) => VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
        .Where(popup => popup.Child is not null).SelectMany(popup => Tree(popup.Child)).OfType<ContentDialog>()
        .Where(dialog => ReadyButton(dialog, "CloseButton") is not null);
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static async Task UntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("The isolated plugin approval UI did not reach its expected state.");
            await Task.Delay(25);
        }
    }
}
