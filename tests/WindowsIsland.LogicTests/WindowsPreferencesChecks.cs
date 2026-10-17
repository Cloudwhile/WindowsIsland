using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
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
        var updateJob = Path.Combine(Path.GetTempPath(), "WindowsIsland Update", "job.json");
        var update = UpdateElevation.Request(executable, updateJob);
        check(update.UseShellExecute && update.Verb == "runas" && update.WindowStyle == ProcessWindowStyle.Hidden
            && update.ArgumentList.SequenceEqual(["--apply-update-worker", updateJob]),
            "Protected updates request UAC through the installed executable with a fixed job path");
        check(UpdateElevation.TryHandle(["--apply-update-worker"], out invalidResult) && invalidResult != 0,
            "Malformed privileged update commands do not start a worker");
        check(!UpdateElevation.TryHandle(["--settings"], out _), "Normal settings launch bypasses the update elevation helper");
        var initialization = InitializationElevation.Request(executable, 42, 1234, new Version(1, 0, 0, 12));
        check(initialization.UseShellExecute && initialization.Verb == "runas" && initialization.WindowStyle == ProcessWindowStyle.Hidden
            && initialization.ArgumentList.SequenceEqual(["--prepare-initialization", "42", "1234", "1.0.0.12"]),
            "Protected initialization requests UAC only for a hidden helper and fixed registration inputs");
        check(InitializationElevation.TryHandle(["--prepare-initialization", "unknown"], out invalidResult) && invalidResult != 0,
            "Malformed initialization helper commands do not start the normal application");
        check(!InitializationElevation.TryHandle(["--settings"], out _), "Normal settings launch bypasses the initialization helper");
        if (OperatingSystem.IsWindows()) CheckProtectedInitializationPreparation(check);


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
    private static void CheckProtectedInitializationPreparation(Action<bool, string> check)
    {
        if (!OperatingSystem.IsWindows() || WindowsIdentity.GetCurrent().User is not { } user) return;
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "initialization-preparation", Guid.NewGuid().ToString("N"));
        var installation = new DirectoryInfo(directory);
        var initialization = Path.Combine(directory, "Initialization");
        Directory.CreateDirectory(initialization);
        File.WriteAllText(Path.Combine(directory, "WindowsIsland.pri"), "verification resources");
        File.WriteAllText(Path.Combine(initialization, "AppxManifest.xml"),
            "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\"><Identity Name=\"WindowsIsland.InitializationVerification\" Version=\"1.0.0.0\" /><Capabilities /></Package>");
        File.WriteAllText(Path.Combine(initialization, "RuntimeManifest.xml"),
            "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\"><Extensions /></Package>");
        var original = new DirectorySecurity();
        original.SetSecurityDescriptorBinaryForm(installation.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm(),
            AccessControlSections.Access);
        var protectedAccess = installation.GetAccessControl(AccessControlSections.Access);
        protectedAccess.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Deny));
        var preparedByHelper = false;
        try
        {
            installation.SetAccessControl(protectedAccess);
            InitializationPreparation.PrepareAsync(directory, null, version =>
            {
                preparedByHelper = true;
                installation.SetAccessControl(original);
                InitializationPreparation.Prepare(directory, version);
                return Task.CompletedTask;
            }).GetAwaiter().GetResult();
            check(preparedByHelper && File.Exists(Path.Combine(directory, "AppxManifest.xml"))
                && InitializationResources.Matches(Path.Combine(directory, "WindowsIsland.pri"), Path.Combine(directory, "resources.pri")),
                "Protected initialization requests only a preparation helper, then leaves registration files ready for the original user");
        }
        finally { installation.SetAccessControl(original); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void CheckRegistry(string executable, Action<bool, string> check)
    {
        var path = @"Software\Cloudwhile\WindowsIsland\Verification\" + Guid.NewGuid().ToString("N");
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(path);
            var store = new RegistryWindowsPreferenceStore(root, disableVirtualization: true);
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
