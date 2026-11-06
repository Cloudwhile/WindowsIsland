using Microsoft.Win32;

namespace WindowsIsland.Services;

internal sealed class WindowsSettingsService
{
    private readonly IWindowsPreferenceStore _store;
    private readonly bool _verification;
    public StartupRegistration Startup { get; }
    public DoNotDisturbPreferences DoNotDisturb { get; }

    public WindowsSettingsService(bool verification)
    {
        _verification = verification;
        _store = verification
            ? new FileWindowsPreferenceStore(Path.Combine(AppContext.BaseDirectory, "verification-system-settings.json"))
            : new RegistryWindowsPreferenceStore(Registry.CurrentUser, disableVirtualization: true);
        Startup = new(_store, Environment.ProcessPath!);
        DoNotDisturb = new(verification ? new FileQuietHoursStore(_store) : new WindowsQuietHoursStore());
    }

    public async Task<bool> SetStartupAsync(bool enabled)
    {
        if (!_verification && InstallationMode.IsPortable()) return false;
        if (_verification)
        {
            await Task.Run(() =>
            {
                _store.Write("Verification", "ElevationRequested", new(PreferenceValueKind.DWord, Number: 1));
                if (_store.Read("Verification", "CancelElevation")?.Number != 1) Startup.Set(enabled);
            });
            if (_store.Read("Verification", "CancelElevation")?.Number == 1) return false;
        }
        else if (!await StartupElevation.ConfigureAsync(enabled)) return false;
        if (await Task.Run(Startup.Read) != (enabled ? StartupState.Enabled : StartupState.Disabled))
            throw new IOException("The startup preference did not match the requested value.");
        return true;
    }

}
