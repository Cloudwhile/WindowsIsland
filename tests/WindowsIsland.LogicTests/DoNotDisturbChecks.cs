using WindowsIsland.Services;

internal static class DoNotDisturbChecks
{
    public static void Run(Action<bool, string> check)
    {
        var store = new QuietHoursFixture();
        var preferences = new DoNotDisturbPreferences(store);
        check(preferences.Read() == DoNotDisturbState.Off && store.Writes == 0, "Reading quiet hours never changes Windows preferences");
        preferences.SetEnabled(true);
        check(preferences.Read() == DoNotDisturbState.On && store.Profile == DoNotDisturbPreferences.PriorityOnly,
            "Enabling do not disturb selects the system priority profile");
        preferences.SetEnabled(true);
        check(store.Writes == 1, "Repeated enabling is idempotent");
        preferences.SetEnabled(false);
        check(preferences.Read() == DoNotDisturbState.Off, "Disabling restores the unrestricted system profile");
        store.Profile = DoNotDisturbPreferences.AlarmsOnly;
        preferences.SetEnabled(true);
        check(store.Profile == DoNotDisturbPreferences.AlarmsOnly && store.Writes == 2, "Enabling preserves an existing alarms-only choice");
        store.Profile = "Unknown";
        check(preferences.Read() == DoNotDisturbState.Unavailable, "Unknown profiles do not appear as disabled");
        ExpectFailure(() => preferences.SetEnabled(true), check, "Unknown profiles cannot be overwritten");
        check(store.Writes == 2, "Rejected changes leave the system untouched");
        store.Profile = DoNotDisturbPreferences.Unrestricted;
        store.IgnoreWrite = true;
        ExpectFailure(() => preferences.SetEnabled(true), check, "Read-back mismatch cannot report success");
        check(preferences.Read() == DoNotDisturbState.Off, "A failed write retains the actual state");
        store.FailRead = true;
        ExpectFailure(() => preferences.SetEnabled(true), check, "Read failures cannot silently enable quiet hours");

        var path = Path.Combine(Path.GetTempPath(), "WindowsIsland.QuietHours." + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var file = new FileWindowsPreferenceStore(path);
            const string application = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\WeChat";
            foreach (var name in new[] { "Enabled", "ShowInActionCenter", "ShowBanner" })
                file.Write(application, name, new(PreferenceValueKind.DWord, Number: 1));
            var isolated = new DoNotDisturbPreferences(new FileQuietHoursStore(file));
            isolated.SetEnabled(true);
            var restored = new DoNotDisturbPreferences(new FileQuietHoursStore(new FileWindowsPreferenceStore(path)));
            check(restored.Read() == DoNotDisturbState.On, "Verification quiet hours persist only in the isolated file");
            check(file.Read(application, "Enabled")?.Number == 1
                && file.Read(application, "ShowInActionCenter")?.Number == 1
                && file.Read(application, "ShowBanner")?.Number == 1,
                "Do not disturb does not alter application notification delivery or banner preferences");
            file.Write(FileQuietHoursStore.Key, "Profile", new(PreferenceValueKind.DWord, Number: 1));
            check(isolated.Read() == DoNotDisturbState.Unavailable, "Invalid verification data cannot report a valid system state");
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }

        var mute = new GameModePolicy();
        mute.Update(true, true, UserNotificationState.QuietTime);
        var tracker = new NotificationTracker();
        tracker.Update([]);
        var notification = new IslandNotification(810, DateTimeOffset.Now, "Fixture", "New message", "During quiet hours");
        check(!mute.IsMuted && tracker.Update([notification]).Single() == notification,
            "Quiet hours keep new notifications available to the island without activating game or full-screen mute");
    }

    private static void ExpectFailure(Action action, Action<bool, string> check, string label)
    {
        try { action(); check(false, label); }
        catch (Exception error) when (error is IOException or NotSupportedException) { check(true, label); }
    }

    private sealed class QuietHoursFixture : IQuietHoursStore
    {
        public string Profile = DoNotDisturbPreferences.Unrestricted;
        public int Writes;
        public bool IgnoreWrite, FailRead;
        public string ReadProfile() => FailRead ? throw new IOException("Fixture read failed") : Profile;
        public void WriteProfile(string profile) { Writes++; if (!IgnoreWrite) Profile = profile; }
    }
}
