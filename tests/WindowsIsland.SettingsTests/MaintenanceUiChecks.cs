using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using WindowsIsland.Components;
using WindowsIsland.Services;

namespace WindowsIsland;

// Included only in the isolated native settings verification application.
internal static class MaintenanceUiChecks
{
    public static async Task RunAsync(SettingsWindow window, SettingsPage page, SettingsStore store, Action<bool, string> check)
    {
        var language = Localization.Language;
        var navigation = Tree(page).OfType<NavigationView>().Single();
        var selected = navigation.SelectedItem;
        var diagnostics = Diagnostics.Store ?? throw new InvalidOperationException("The isolated diagnostic store is required.");
        var actualPath = typeof(DiagnosticStore).GetField("_path", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(diagnostics) as string;
        var expectedPath = Path.Combine(AppContext.BaseDirectory, "verification-diagnostics", "diagnostics.json");
        check(actualPath is not null && string.Equals(Path.GetFullPath(actualPath), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase),
            "Maintenance UI checks are restricted to the output directory's isolated diagnostic store");
        // A caller's Check delegate normally throws. Keep this guard independent of it.
        if (actualPath is null || !string.Equals(Path.GetFullPath(actualPath), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to modify a diagnostic store outside the isolated verification directory.");
        var enabled = diagnostics.Enabled;
        try
        {
            diagnostics.SetEnabled(true);
            Select(navigation, "diagnostics");
            await Until(() => Tree(page).OfType<SettingsDiagnostics>().Any());
            await CheckDiagnosticsAsync(page, diagnostics, check);
            await CheckUpdatesAsync(store, check);
        }
        finally
        {
            diagnostics.SetEnabled(enabled);
            Localization.Configure(language);
            navigation.SelectedItem = selected;
            page.Refresh();
            window.Activate();
        }
    }

    private static async Task CheckDiagnosticsAsync(SettingsPage page, DiagnosticStore diagnostics, Action<bool, string> check)
    {
        var id = diagnostics.Record(DiagnosticArea.Diagnostics, "UserInterface", new InvalidOperationException())
            ?? throw new InvalidOperationException("The isolated failure fixture was not recorded.");
        diagnostics.Reports.Invalidate();
        page.Refresh();
        await Until(() => Tree(page).OfType<SettingsExpander>().Any(row => Id(row) == "DiagnosticEntry." + id));
        var firstRequest = diagnostics.Reports.GetAsync();
        var expectedReport = await firstRequest;
        Invoke(Button(page, "GenerateDiagnostics"));
        await Until(() => Button(page, "GenerateDiagnostics").IsEnabled && Status(page).Message == Localization.Get("DiagnosticsGenerated"));
        check(ReferenceEquals(firstRequest, diagnostics.Reports.GetAsync()), "Manual report generation reuses the existing diagnostic report cache");
        Invoke(Button(page, "ViewDiagnostics"));
        var preview = Tree(page).OfType<TextBox>().Single(box => Id(box) == "DiagnosticDetails");
        await Until(() => preview.Visibility == Visibility.Visible && Button(page, "ViewDiagnostics").IsEnabled);
        check(SameReport(preview.Text, expectedReport) && ReferenceEquals(firstRequest, diagnostics.Reports.GetAsync()),
            "Diagnostic preview shows the same cached report JSON");
        Invoke(Button(page, "CopyDiagnostics"));
        await Until(() => Button(page, "CopyDiagnostics").IsEnabled && Status(page).Message == Localization.Get("DiagnosticsCopied"));
        var clipboard = Clipboard.GetContent();
        check(clipboard.Contains(StandardDataFormats.Text) && await clipboard.GetTextAsync() == expectedReport
            && SameReport(preview.Text, expectedReport) && ReferenceEquals(firstRequest, diagnostics.Reports.GetAsync()),
            "Copy, preview and the report cache contain identical diagnostic JSON");

        Invoke(Button(page, "ClearDiagnostics"));
        await Until(() => Dialogs(page).Any(dialog => Id(dialog) == "ClearDiagnosticsConfirmation"));
        var dialog = Dialogs(page).Single(item => Id(item) == "ClearDiagnosticsConfirmation");
        check(dialog.DefaultButton == ContentDialogButton.Close && dialog.XamlRoot == page.XamlRoot
            && dialog.Content?.ToString() == Localization.Get("DiagnosticsClearConfirmMessage"),
            "Clearing diagnostics explains its effect and defaults to cancel on the active XAML root");
        Invoke(DialogButton(dialog, "CloseButton", dialog.CloseButtonText));
        await Until(() => !Dialogs(page).Any() && Button(page, "ClearDiagnostics").IsEnabled);
        check(diagnostics.Snapshot().Any(entry => entry.Id == id) && preview.Visibility == Visibility.Visible && SameReport(preview.Text, expectedReport),
            "Canceling diagnostic clear preserves the records and current report preview");

        Invoke(Button(page, "ClearDiagnostics"));
        await Until(() => Dialogs(page).Any(item => Id(item) == "ClearDiagnosticsConfirmation"));
        dialog = Dialogs(page).Single(item => Id(item) == "ClearDiagnosticsConfirmation");
        Invoke(DialogButton(dialog, "PrimaryButton", dialog.PrimaryButtonText));
        await Until(() => !Dialogs(page).Any() && diagnostics.Snapshot().Length == 0
            && preview.Visibility == Visibility.Collapsed && Status(page).Message == Localization.Get("DiagnosticsCleared"));
        check(preview.Text.Length == 0 && !Button(page, "ClearDiagnostics").IsEnabled
            && !ReferenceEquals(firstRequest, diagnostics.Reports.GetAsync()),
            "Confirmed clear removes only isolated records, collapses preview and invalidates the report cache");
        check(Tree(page).OfType<TextBlock>().Any(text => Visible(text) && text.Text == Localization.Get("DiagnosticsNoFailures")),
            "An empty diagnostic overview shows the no-failures result without an exception");

        var successId = diagnostics.Record(DiagnosticArea.WeChat, "AccessibilityEnabled")!;
        var failureId = diagnostics.Record(DiagnosticArea.WeChat, "AccessibilityUnavailable", new InvalidOperationException())!;
        page.Refresh();
        await Until(() => Tree(page).OfType<SettingsExpander>().Any(row => Id(row) == "DiagnosticEntry." + failureId));
        var success = Tree(page).OfType<SettingsExpander>().Single(row => Id(row) == "DiagnosticEntry." + successId);
        var failure = Tree(page).OfType<SettingsExpander>().Single(row => Id(row) == "DiagnosticEntry." + failureId);
        Invoke(Button(failure, "DiagnosticEntry." + failureId + ".Toggle"));
        Invoke(Button(success, "DiagnosticEntry." + successId + ".Toggle"));
        await Until(() => failure.IsExpanded && success.IsExpanded);
        check(failure.Header.Tone == StatusTone.Critical && success.Header.Tone == StatusTone.Success
            && failure.Header.Detail.Contains(DiagnosticPresentation.Area(diagnostics.Snapshot().Single(entry => entry.Id == failureId))),
            "Diagnostic records expose localized type, time, result and occurrence count with expandable details");
        diagnostics.Record(DiagnosticArea.WeChat, "AccessibilityUnavailable", new InvalidOperationException());
        for (var index = 0; index < 5; index++) page.Refresh();
        await Task.Delay(1200); // Exercise the real SettingsWindow refresh timer as well.
        var updated = diagnostics.Snapshot().Single(entry => entry.Id == failureId);
        check(ReferenceEquals(failure, Tree(page).OfType<SettingsExpander>().Single(row => Id(row) == "DiagnosticEntry." + failureId))
            && failure.IsExpanded && success.IsExpanded && updated.Count == 2
            && failure.Header.Status == DiagnosticPresentation.Summary(updated),
            "Count updates and periodic refresh preserve diagnostic row instances and expanded state");
        var language = Localization.Language;
        Localization.Configure(language == "en-US" ? "zh-CN" : "en-US");
        page.Refresh();
        check(failure.IsExpanded && success.IsExpanded && failure.Header.Title == DiagnosticPresentation.Title(updated)
            && failure.Header.Status == DiagnosticPresentation.Summary(updated),
            "Switching language refreshes diagnostic labels without collapsing details");
        Localization.Configure(language);
        page.Refresh();
    }

    private static async Task CheckUpdatesAsync(SettingsStore mainStore, Action<bool, string> check)
    {
        var settings = new SettingsStore(Path.Combine(AppContext.BaseDirectory, "verification-maintenance-settings.json"));
        settings.Save(mainStore.Current with { IncludePrereleaseUpdates = false });
        var pending = NewQuery();
        var queries = 0;
        using var service = new UpdateCheckService(settings, (_, token) =>
        {
            Interlocked.Increment(ref queries);
            return pending.Task.WaitAsync(token);
        }, TimeSpan.FromSeconds(15));
        var restarts = 0;
        using var updates = new SettingsUpdates(settings, service, () => restarts++);
        var host = new Window { Content = new ScrollViewer { Content = SettingsSection.Page(updates), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        host.AppWindow.Resize(new(520, 720));
        try
        {
            host.Activate();
            await Until(() => updates.IsLoaded && updates.ActualWidth > 0);
            var checkAction = Button(updates, "CheckForUpdates");
            var install = Button(updates, "InstallUpdate");
            var available = Tree(updates).OfType<TextBlock>().FirstOrDefault(text => Id(text) == "UpdateAvailableStatus");
            check(!install.IsEnabled && (available is null || !Visible(available)),
                "An unchecked update page hides the new-version region and keeps install unavailable");

            Invoke(checkAction);
            await Until(() => service.State.Busy && Volatile.Read(ref queries) == 1 && !checkAction.IsEnabled);
            var request = service.CheckAsync();
            check(ReferenceEquals(request, service.CheckAsync()) && queries == 1
                && Button(updates, "CancelUpdateCheck").Visibility == Visibility.Visible && !install.IsEnabled,
                "A running update query disables duplicate checks and exposes cancellation");
            Invoke(Button(updates, "CancelUpdateCheck"));
            await Until(() => service.State.Canceled && checkAction.IsEnabled);
            check(UpdateStatus(updates).Message == Localization.Get("UpdateCheckCanceled") && !install.IsEnabled,
                "Canceling a check restores a retryable result without an install candidate");

            pending = NewQuery();
            Invoke(checkAction);
            await Until(() => service.State.Busy && Volatile.Read(ref queries) == 2);
            pending.SetResult(null);
            await Until(() => service.State.Checked && checkAction.IsEnabled
                && UpdateStatus(updates).Message == Localization.Get("UpdateNone"));
            available = Tree(updates).OfType<TextBlock>().FirstOrDefault(text => Id(text) == "UpdateAvailableStatus");
            check(UpdateStatus(updates).Message == Localization.Get("UpdateNone") && UpdateStatus(updates).Severity == InfoBarSeverity.Success
                && !install.IsEnabled && (available is null || !Visible(available))
                && !Tree(updates).OfType<TextBlock>().Any(text => Visible(text) && text.Text == Localization.Get("UpdateUpToDate")),
                "No-update state has one check result and no duplicate latest-version row");

            ReleaseVersion.TryParse("v999.0.0", out var version);
            var candidate = new GitHubRelease(version!, "v999.0.0", "Maintenance fixture", "## Fixture notes\n\nA fixed native Markdown fixture.",
                new Uri("https://example.invalid/releases/v999.0.0"), false,
                new("WindowsIsland-v999.0.0-x64.exe", new Uri("https://example.invalid/installer.exe"), 2 * 1024 * 1024),
                new("WindowsIsland-v999.0.0-x64.sha256", new Uri("https://example.invalid/checksum.sha256"), 64));
            pending = NewQuery();
            Invoke(checkAction);
            await Until(() => service.State.Busy && Volatile.Read(ref queries) == 3);
            pending.SetResult(candidate);
            await Until(() => install.IsEnabled);
            check(Tree(updates).OfType<TextBlock>().Any(text => Id(text) == "UpdateAvailableStatus" && Visible(text) && text.Text == candidate.Tag)
                && Tree(updates).OfType<TextBlock>().Single(text => Id(text) == "UpdatePackageSize").Text == SettingsUpdates.FormatSize(2 * 1024 * 1024)
                && Tree(updates).OfType<MarkdownText>().Single().Text == candidate.Notes,
                "An available update shows its target version, installer size, notes and install action");
            var channel = Tree(updates).OfType<ToggleSwitch>().Single(toggle => Id(toggle) == "PrereleaseUpdatesToggle");
            channel.IsOn = true;
            await Until(() => service.State.Candidate is null && !install.IsEnabled);
            check(!service.State.Checked && queries == 3, "Changing update channel revokes the previous candidate until another explicit check");

            pending = NewQuery();
            Invoke(checkAction);
            await Until(() => service.State.Busy && Volatile.Read(ref queries) == 4);
            pending.SetException(new System.Net.Http.HttpRequestException("Fixed verification failure"));
            await Until(() => service.State.ErrorKey is not null && checkAction.IsEnabled
                && UpdateStatus(updates).Severity == InfoBarSeverity.Error);
            check(UpdateStatus(updates).Severity == InfoBarSeverity.Error && !install.IsEnabled,
                "A failed update query shows an error and keeps the check action available for retry");
            pending = NewQuery();
            Invoke(checkAction);
            await Until(() => service.State.Busy && Volatile.Read(ref queries) == 5);
            pending.SetResult(null);
            await Until(() => service.State.Checked && checkAction.IsEnabled
                && UpdateStatus(updates).Message == Localization.Get("UpdateNone"));
            check(UpdateStatus(updates).Message == Localization.Get("UpdateNone") && queries == 5 && restarts == 0,
                "Retry succeeds using injected queries without downloading, installing or restarting");
        }
        finally { host.Close(); }
    }

    // Native multiline TextBox normalizes line endings; clipboard/export still require the original string.
    private static bool SameReport(string rendered, string report) => JsonNode.DeepEquals(JsonNode.Parse(rendered), JsonNode.Parse(report));
    private static TaskCompletionSource<GitHubRelease?> NewQuery() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static InfoBar Status(DependencyObject root) => Tree(root).OfType<InfoBar>().Single(bar => Id(bar) == "DiagnosticStatus");
    private static InfoBar UpdateStatus(DependencyObject root) => Tree(root).OfType<InfoBar>().Single(bar => Id(bar) == "UpdateStatus");
    private static string Id(DependencyObject item) => AutomationProperties.GetAutomationId(item);
    private static Button Button(DependencyObject root, string id) => Tree(root).OfType<Button>().Single(button => Id(button) == id);
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static Button DialogButton(ContentDialog dialog, string name, string label) => ReadyDialogButton(dialog, name, label)
        ?? throw new InvalidOperationException("The visible diagnostic confirmation button is not ready: " + name);
    private static IEnumerable<ContentDialog> Dialogs(FrameworkElement page) => VisualTreeHelper.GetOpenPopupsForXamlRoot(page.XamlRoot)
        .Where(popup => popup.Child is not null).SelectMany(popup => Tree(popup.Child)).OfType<ContentDialog>()
        .Where(dialog => dialog.IsLoaded && dialog.Visibility == Visibility.Visible && dialog.ActualWidth > 0 && dialog.ActualHeight > 0
            && ReadyDialogButton(dialog, "CloseButton", dialog.CloseButtonText) is not null
            && (string.IsNullOrEmpty(dialog.PrimaryButtonText) || ReadyDialogButton(dialog, "PrimaryButton", dialog.PrimaryButtonText) is not null));
    private static Button? ReadyDialogButton(ContentDialog dialog, string name, string label) => Tree(dialog).OfType<Button>()
        .FirstOrDefault(button => button.IsLoaded && button.Visibility == Visibility.Visible && button.IsEnabled
            && button.ActualWidth > 0 && button.ActualHeight > 0
            && (button.Name == name || label.Length > 0 && button.Content?.ToString() == label));
    private static void Select(NavigationView navigation, string tag) => navigation.SelectedItem = navigation.MenuItems
        .Concat(navigation.FooterMenuItems).OfType<NavigationViewItem>().Single(item => item.Tag is string value && value == tag);
    private static bool Visible(FrameworkElement element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement { Visibility: Visibility.Collapsed }) return false;
        return element.IsLoaded;
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
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The maintenance verification control did not reach its expected state.");
            await Task.Delay(30);
        }
    }
}
