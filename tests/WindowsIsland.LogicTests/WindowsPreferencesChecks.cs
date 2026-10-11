using System.Diagnostics;
using Microsoft.Win32;
using WindowsIsland.Services;

internal static class WindowsPreferencesChecks
{
    public static void Run(Action<bool, string> check)
    {
        var executable = Path.Combine(Path.GetTempPath(), "Windows Island 测试", "WindowsIsland.exe");
        var store = new MemoryPreferences();
        var startup = new StartupRegistration(store, executable);
        check(startup.Read() == StartupState.Disabled, "Startup remains opt-in when no entry exists");
        store.Write(StartupRegistration.RunKey, "OtherApplication", new(PreferenceValueKind.String, Text: "other.exe"));
        startup.Set(true);
        check(startup.Read() == StartupState.Enabled && startup.Command == "\"" + executable + "\" --startup",
            "Startup quotes Unicode and spaced paths and requests a background launch");
        store.Write(StartupRegistration.ApprovalKey, StartupRegistration.ValueName, new(PreferenceValueKind.Binary, Bytes: [3, 0, 0, 0]));
        check(startup.Read() == StartupState.Blocked, "Windows-disabled startup entries are displayed as disabled");
        startup.Set(true);
        check(startup.Read() == StartupState.Enabled, "An explicit startup request clears this application's previous disabled state");
        startup.Set(false);
        check(startup.Read() == StartupState.Disabled && store.Read(StartupRegistration.RunKey, "OtherApplication")?.Text == "other.exe",
            "Disabling startup preserves other applications");
        var other = new PreferenceValue(PreferenceValueKind.String, Text: "\"other\\WindowsIsland.exe\" --startup");
        store.Write(StartupRegistration.RunKey, StartupRegistration.ValueName, other);
        check(startup.Read() == StartupState.OtherInstallation, "Startup reports an entry belonging to another installation");
        ExpectFailure(() => startup.Set(false), check, "Disabling this copy cannot delete another installation's startup entry");
        check(store.Read(StartupRegistration.RunKey, StartupRegistration.ValueName) == other, "Another installation's startup entry survives a rejected removal");
        store.FailApprovalRemoval = true;
        ExpectFailure(() => startup.Set(true), check, "Startup surfaces a failure to apply the system preference");
        check(store.Read(StartupRegistration.RunKey, StartupRegistration.ValueName) == other, "A failed startup change restores the previous command");
        foreach (var invalid in new[] { "relative/WindowsIsland.exe", executable + "\"", Path.Combine(Path.GetTempPath(), "Other.exe") })
            ExpectFailure(() => StartupRegistration.BuildCommand(invalid), check, "Invalid startup commands are rejected: " + Path.GetFileName(invalid));
        var request = StartupElevation.Request(true, executable, 42, 1234);
        check(request.UseShellExecute && request.Verb == "runas" && request.WindowStyle == ProcessWindowStyle.Hidden
            && request.ArgumentList.SequenceEqual(["--configure-startup", "enable", "42", "1234"]),
            "Startup requests UAC for a hidden helper with separate arguments and the requesting process identity");
        check(StartupElevation.TryHandle(["--configure-startup", "unknown"], out var invalidResult) && invalidResult != 0,
            "Malformed privileged commands are handled without starting the normal application");
        check(!StartupElevation.TryHandle(["--settings"], out _), "Normal launch arguments retain the regular application startup");


        CheckFilePersistence(executable, check);
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("WINDOWSISLAND_TEST_REGISTRY") == "1")
            CheckRegistry(executable, check);
    }

    private static void CheckFilePersistence(string executable, Action<bool, string> check)
    {
        var path = Path.Combine(Path.GetTempPath(), "WindowsIsland.SystemPreferences." + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new FileWindowsPreferenceStore(path);
            new StartupRegistration(store, executable).Set(true);
            new DoNotDisturbPreferences(new FileQuietHoursStore(store)).SetEnabled(true);
            var restored = new FileWindowsPreferenceStore(path);
            check(new StartupRegistration(restored, executable).Read() == StartupState.Enabled
                && new DoNotDisturbPreferences(new FileQuietHoursStore(restored)).Read() == DoNotDisturbState.On,
                "Desktop verification keeps startup and do not disturb persistence in an isolated file");
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void CheckRegistry(string executable, Action<bool, string> check)
    {
        var path = @"Software\Cloudwhile\WindowsIsland\Verification\" + Guid.NewGuid().ToString("N");
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(path);
            var store = new RegistryWindowsPreferenceStore(root);
            var startup = new StartupRegistration(store, executable);
            startup.Set(true);
            check(startup.Read() == StartupState.Enabled, "The native registry adapter persists startup in an isolated test hive");
            startup.Set(false);
            check(startup.Read() == StartupState.Disabled, "The native registry adapter removes only its startup value");

        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }

    private static void ExpectFailure(Action action, Action<bool, string> check, string description)
    {
        try { action(); check(false, description); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException) { check(true, description); }
    }

    private sealed class MemoryPreferences : IWindowsPreferenceStore
    {
        private readonly Dictionary<(string Path, string Name), PreferenceValue> _values = [];
        public bool FailApprovalRemoval { get; set; }
        public string[] SubKeys(string path) => _values.Keys.Where(key => key.Path.StartsWith(path + "\\", StringComparison.Ordinal))
            .Select(key => key.Path[(path.Length + 1)..].Split('\\')[0]).Distinct().ToArray();
        public PreferenceValue? Read(string path, string name) => _values.GetValueOrDefault((path, name));
        public void Write(string path, string name, PreferenceValue value) => _values[(path, name)] = value;
        public void Remove(string path, string name)
        {
            if (FailApprovalRemoval && path == StartupRegistration.ApprovalKey) { FailApprovalRemoval = false; throw new IOException("Fixture write failure."); }
            _values.Remove((path, name));
        }
    }
}
