using System.Collections.Concurrent;
using Microsoft.Win32;

namespace WindowsIsland.Services;

internal sealed class WindowsSettingsService
{
    private readonly IWindowsPreferenceStore _store;
    private readonly bool _verification;
    private readonly ConcurrentDictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    public StartupRegistration Startup { get; }
    public NotificationBannerPreferences Banners { get; }

    public WindowsSettingsService(bool verification)
    {
        _verification = verification;
        _store = verification
            ? new FileWindowsPreferenceStore(Path.Combine(AppContext.BaseDirectory, "verification-system-settings.json"))
            : new RegistryWindowsPreferenceStore(Registry.CurrentUser);
        Startup = new(_store, Environment.ProcessPath!);
        Banners = new(_store);
    }

    public async Task<bool> SetStartupAsync(bool enabled)
    {
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

    public string SenderName(string identity) => _verification ? identity : _names.GetOrAdd(identity, NotificationSenderNames.Read);
}
