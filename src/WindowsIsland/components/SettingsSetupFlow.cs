using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class SettingsSetupFlow : StackPanel
{
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 3 };
    private readonly TextBlock _status = IslandTheme.Text("", 12, secondary: true);
    private readonly SettingsRow _check;
    private readonly SettingsRow _finish;

    public SettingsSetupFlow(SettingsRow initialization, SettingsRow permission, IconActionButton check,
        IconActionButton complete, PortableRegistrationSettings portable)
    {
        Spacing = SettingsTokens.ControlSpacing;
        AutomationProperties.SetAutomationId(this, "InitializationSection");
        AutomationProperties.SetAutomationId(_status, "InitializationStatus");
        LocalizedUI.SetAutomationName(_progress, "SetupProgress");
        _status.TextWrapping = TextWrapping.Wrap;
        Children.Add(new SettingsContent(false, LocalizedUI.Text("SetupOptionalHint", 12, secondary: true), _progress, _status));
        Children.Add(new SettingsSection("SetupStepInitialize", initialization));
        Children.Add(new SettingsSection("SetupStepAccess", permission));
        _check = new SettingsRow(Symbol.Refresh, "SetupVerify", check);
        Children.Add(new SettingsSection("SetupStepVerify", _check));
        _finish = new SettingsRow(Symbol.Accept, "SetupFinish", complete);
        Children.Add(new SettingsSection("SetupStepFinish", _finish));
        Children.Add(portable);
    }

    public void Refresh(SettingsSnapshot state, AppSettings settings, bool busy)
    {
        var ready = !settings.SystemNotifications || state.Initialized && state.Access == NotificationAccess.Allowed;
        _progress.Value = ready ? 3 : state.Initialized ? 1 : 0;
        _progress.IsIndeterminate = busy;
        _status.Text = Localization.Get(!settings.SystemNotifications ? "SetupSystemOptional" : ready ? "Complete" : "IncompleteSetup");
        _check.Status = Localization.Get(ready ? "Complete" : "Pending");
        _check.Tone = ready ? StatusTone.Success : StatusTone.Neutral;
        _finish.Status = Localization.Get(ready ? "SetupReturnOverview" : "SetupFinishPending");
    }
}
