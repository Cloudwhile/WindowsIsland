using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal static class PluginConsentDialog
{
    public static ContentDialog CreateInstall(Contracts.PluginCatalogEntry entry)
    {
        var content = new StackPanel { Spacing = SettingsTokens.SectionSpacing };
        AddIdentity(content, entry.Id, entry.Name, entry.Version, entry.DeveloperId);
        content.Children.Add(LocalizedUI.Text("PluginDefaultSourceNotice", SettingsTokens.DescriptionTextSize, secondary: true));
        content.Children.Add(LocalizedUI.Text("PluginInstallationReview", SettingsTokens.BodyTextSize));
        AddValue(content, "PluginPermissionsRequested", entry.Permissions.Length == 0 ? Localization.Get("PluginPermissionsNone")
            : string.Join(Environment.NewLine, entry.Permissions), "PluginInstallDeclarations");
        var unavailable = entry.Permissions.Where(permission => permission is not (Contracts.PluginPermissions.Notifications
            or Contracts.PluginPermissions.Settings or Contracts.PluginPermissions.Network)
            && !(permission is Contracts.PluginPermissions.Process or Contracts.PluginPermissions.Automation
                && PluginDesktopPolicy.MatchesPublisher(entry.Id, entry.DeveloperId, entry.RepositoryUrl))).ToArray();
        if (unavailable.Length > 0)
            AddValue(content, "PluginPermissionsDenied", string.Join(Environment.NewLine, unavailable), "PluginInstallUnavailable");
        return Create("PluginInstallationReviewTitle", "PluginInstall", content, "PluginInstallationReviewDialog");
    }

    public static ContentDialog CreateEnable(PluginConsentDescriptor descriptor)
    {
        var content = new StackPanel { Spacing = SettingsTokens.SectionSpacing };
        AddIdentity(content, descriptor.PluginId, descriptor.Name, descriptor.Version, descriptor.DeveloperId,
            descriptor.DeveloperName ?? descriptor.Author);
        AddValue(content, "PluginSource", Localization.Get(descriptor.SourceKind == Contracts.PluginSourceKind.Official
            ? "PluginOfficial" : "PluginManualRestricted"), "PluginConsentSource");
        content.Children.Add(LocalizedUI.Text("PluginConsentEnableNotice", SettingsTokens.BodyTextSize));
        var confirmations = new List<CheckBox>();
        if (descriptor.Terms is { } terms)
        {
            var section = Group("PluginConsentTerms");
            AddValue(section, "PluginConsentTermsRevision", terms.Revision, "PluginConsentTermsRevision");
            section.Children.Add(ReadOnlyText(terms.Text, "PluginConsentTermsText", "PluginConsentTerms"));
            var accepted = Confirmation("PluginConsentAcceptTerms", "PluginConsentAcceptTerms");
            section.Children.Add(accepted);
            confirmations.Add(accepted);
            content.Children.Add(section);
        }
        if (descriptor.Network is { } network)
        {
            var section = Group("PluginConsentNetwork");
            if (network.Notice is { } notice)
                section.Children.Add(ReadOnlyText(notice, "PluginConsentNetworkText", "PluginConsentNetwork"));
            AddValue(section, "PluginConsentDestinations", string.Join(Environment.NewLine, network.Origins), "PluginConsentDestinations");
            AddValue(section, "PluginConsentPurpose", network.Purpose, "PluginConsentPurpose");
            AddValue(section, "PluginConsentDataCategories", string.Join(Environment.NewLine, network.DataCategories), "PluginConsentDataCategories");
            var accepted = Confirmation("PluginConsentAcceptNetwork", "PluginConsentAcceptNetwork");
            section.Children.Add(accepted);
            confirmations.Add(accepted);
            content.Children.Add(section);
        }
        var dialog = Create("PluginConsentEnableTitle", "PluginConsentEnable", content, "PluginConsentDialog");
        void Update() => dialog.IsPrimaryButtonEnabled = confirmations.All(check => check.IsChecked == true);
        foreach (var check in confirmations)
        {
            check.Checked += (_, _) => Update();
            check.Unchecked += (_, _) => Update();
        }
        Update();
        return dialog;
    }

    private static ContentDialog Create(string title, string primary, FrameworkElement content, string id)
    {
        var dialog = new ContentDialog
        {
            Title = Localization.Get(title), PrimaryButtonText = Localization.Get(primary),
            CloseButtonText = Localization.Get("PluginCancel"), DefaultButton = ContentDialogButton.Close,
            Content = new ScrollViewer
            {
                Content = content, MaxHeight = 440, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            }
        };
        AutomationProperties.SetAutomationId(dialog, id);
        return dialog;
    }

    internal static void AddIdentity(Panel parent, string id, string name, string version, string developerId, string? developerName = null)
    {
        AddValue(parent, "PluginConsentName", name, "PluginConsentName");
        AddValue(parent, "PluginIdentifier", id, "PluginConsentIdentifier");
        AddValue(parent, "PluginVersion", version, "PluginConsentVersion");
        AddValue(parent, "PluginDeveloper", string.IsNullOrWhiteSpace(developerName) ? developerId
            : string.IsNullOrWhiteSpace(developerId) ? developerName : developerName + " (" + developerId + ")", "PluginConsentDeveloper");
    }

    internal static StackPanel Group(string key)
    {
        var panel = new StackPanel { Spacing = SettingsTokens.RowPadding };
        panel.Children.Add(SettingsSection.Heading(key));
        return panel;
    }

    internal static void AddValue(Panel parent, string label, string value, string id)
    {
        var field = new SettingsField(label, value, id);
        field.Value.IsTextSelectionEnabled = true;
        parent.Children.Add(field);
    }

    internal static TextBox ReadOnlyText(string value, string id, string label)
    {
        var text = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinWidth = 0, MaxHeight = 220, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        text.Text = value;
        AutomationProperties.SetAutomationId(text, id);
        LocalizedUI.SetAutomationName(text, label);
        return text;
    }

    private static CheckBox Confirmation(string label, string id)
    {
        var check = new CheckBox { IsChecked = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        check.Content = LocalizedUI.Text(label, SettingsTokens.BodyTextSize);
        AutomationProperties.SetAutomationId(check, id);
        LocalizedUI.SetAutomationName(check, label);
        return check;
    }
}
