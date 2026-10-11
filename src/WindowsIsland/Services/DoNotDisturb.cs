namespace WindowsIsland.Services;

internal interface IQuietHoursStore
{
    string ReadProfile();
    void WriteProfile(string profile);
}

internal enum DoNotDisturbState { Unavailable, Off, On }

internal sealed class DoNotDisturbPreferences(IQuietHoursStore store)
{
    internal const string Unrestricted = "Microsoft.QuietHoursProfile.Unrestricted";
    internal const string PriorityOnly = "Microsoft.QuietHoursProfile.PriorityOnly";
    internal const string AlarmsOnly = "Microsoft.QuietHoursProfile.AlarmsOnly";

    public DoNotDisturbState Read() => Parse(store.ReadProfile());

    public void SetEnabled(bool enabled)
    {
        var state = Read();
        if (state == DoNotDisturbState.Unavailable)
            throw new NotSupportedException("Unknown quiet hours profile.");
        // Preserve a pre-existing alarms-only profile when it already satisfies the request.
        if ((state == DoNotDisturbState.On) == enabled) return;
        store.WriteProfile(enabled ? PriorityOnly : Unrestricted);
        if (Read() != (enabled ? DoNotDisturbState.On : DoNotDisturbState.Off))
            throw new IOException("Windows did not apply the quiet hours profile.");
    }

    internal static DoNotDisturbState Parse(string profile) => profile switch
    {
        Unrestricted => DoNotDisturbState.Off,
        PriorityOnly or AlarmsOnly => DoNotDisturbState.On,
        _ => DoNotDisturbState.Unavailable
    };
}

// Verification instances never touch the user's actual Windows settings.
internal sealed class FileQuietHoursStore(IWindowsPreferenceStore store) : IQuietHoursStore
{
    internal const string Key = "Verification.QuietHours";
    public string ReadProfile()
    {
        var value = store.Read(Key, "Profile");
        return value is null ? DoNotDisturbPreferences.Unrestricted
            : value.Kind == PreferenceValueKind.String ? value.Text ?? "" : "";
    }

    public void WriteProfile(string profile) => store.Write(Key, "Profile", new(PreferenceValueKind.String, Text: profile));
}
