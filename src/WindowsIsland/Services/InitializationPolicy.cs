using Microsoft.Win32;

namespace WindowsIsland.Services;

internal static class InitializationPolicy
{
    private const string SettingsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock";
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\Appx";
    private static readonly string[] RequiredValues = ["AllowAllTrustedApps", "AllowDevelopmentWithoutDevLicense"];

    internal static bool IsEnabled()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var policy = machine.OpenSubKey(PolicyKey);
        using var settings = machine.OpenSubKey(SettingsKey);
        return RequiredValues.All(name => Enabled(policy?.GetValue(name), settings?.GetValue(name)));
    }

    internal static bool Enabled(object? policy, object? setting) =>
        (policy ?? setting) is int value && value == 1;

    internal static void EnsureEnabled()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var policy = machine.OpenSubKey(PolicyKey);
        // A managed policy takes precedence. Never override an administrator's restriction.
        if (RequiredValues.Any(name => policy?.GetValue(name) is { } value && !Enabled(value, null)))
            throw new UnauthorizedAccessException("Package development is disabled by system policy.");
        using var settings = machine.CreateSubKey(SettingsKey, writable: true)
            ?? throw new UnauthorizedAccessException("Unable to open package development settings.");
        foreach (var name in RequiredValues)
            if (!Enabled(policy?.GetValue(name), settings.GetValue(name)))
                settings.SetValue(name, 1, RegistryValueKind.DWord);
        if (!IsEnabled()) throw new IOException("Package development settings were not applied.");
    }
}
