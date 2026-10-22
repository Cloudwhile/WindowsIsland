using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class PortableRegistrationSettings : Grid
{
    public PortableRegistrationSettings(Action exit, bool verification)
    {
        if (!InstallationMode.IsPortable() || verification) { Visibility = Visibility.Collapsed; return; }
        var remove = new IconActionButton(Symbol.DisconnectDrive, "ActionUnregisterPackage", "UnregisterPortablePackage");
        var row = new SettingsRow(Symbol.Permissions, "UnregisterPackage", remove);
        Children.Add(row);
        remove.Click += async (_, _) =>
        {
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = Localization.Get("UnregisterPackage"),
                Content = Localization.Get("UnregisterPackageConfirm"),
                PrimaryButtonText = Localization.Get("UnregisterPackage"), CloseButtonText = Localization.Get("ActionCancel"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            try { PortableRegistration.ScheduleRemoval(); exit(); }
            catch (Exception error)
            {
                Diagnostics.Capture(DiagnosticArea.Settings, error, "ErrorInitialize");
                row.Status = Localization.Get("ErrorUnregisterPackage");
                row.Tone = StatusTone.Critical;
            }
        };
    }
}
