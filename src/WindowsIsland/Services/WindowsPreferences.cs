namespace WindowsIsland.Services;

internal enum PreferenceValueKind { String, DWord, Binary, Unsupported }
internal sealed record PreferenceValue(PreferenceValueKind Kind, string? Text = null, int Number = 0, byte[]? Bytes = null);

internal interface IWindowsPreferenceStore
{
    string[] SubKeys(string path);
    PreferenceValue? Read(string path, string name);
    void Write(string path, string name, PreferenceValue value);
    void Remove(string path, string name);
}

internal enum StartupState { Disabled, Enabled, Blocked, OtherInstallation }
internal sealed class StartupRegistration(IWindowsPreferenceStore store, string executable)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovalKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ValueName = "WindowsIsland";
    public string Command { get; } = BuildCommand(executable);

    internal static string BuildCommand(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.IndexOfAny(['"', '\r', '\n', '\0']) >= 0
            || !Path.GetFileName(executable).Equals("WindowsIsland.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid startup executable.", nameof(executable));
        var command = "\"" + Path.GetFullPath(executable) + "\" --startup";
        if (command.Length > 260) throw new ArgumentException("The startup command is too long.", nameof(executable));
        return command;
    }

    public StartupState Read()
    {
        var value = store.Read(RunKey, ValueName);
        if (value is null) return StartupState.Disabled;
        if (value.Kind != PreferenceValueKind.String || !string.Equals(value.Text, Command, StringComparison.OrdinalIgnoreCase))
            return StartupState.OtherInstallation;
        var approval = store.Read(ApprovalKey, ValueName);
        return approval?.Kind == PreferenceValueKind.Binary && approval.Bytes is { Length: > 0 } bytes && bytes[0] is 3 or 7
            ? StartupState.Blocked : StartupState.Enabled;
    }

    public void Set(bool enabled)
    {
        var previous = store.Read(RunKey, ValueName);
        if (previous?.Kind == PreferenceValueKind.Unsupported) throw new InvalidOperationException("Unsupported startup entry.");
        if (!enabled && previous is not null && (previous.Kind != PreferenceValueKind.String
            || !string.Equals(previous.Text, Command, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Another installation owns the startup entry.");
        try
        {
            if (enabled) store.Write(RunKey, ValueName, new(PreferenceValueKind.String, Text: Command));
            else store.Remove(RunKey, ValueName);
            store.Remove(ApprovalKey, ValueName);
        }
        catch
        {
            if (previous is null) store.Remove(RunKey, ValueName);
            else store.Write(RunKey, ValueName, previous);
            throw;
        }
    }
}

internal sealed record BannerPreference(string Identity, bool Hidden, bool Available);
internal sealed class NotificationBannerPreferences(IWindowsPreferenceStore store)
{
    public const string SettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";

    public IReadOnlyList<BannerPreference> Read() => store.SubKeys(SettingsKey)
        .Where(IsApplicationIdentity).Select(ReadApplication).OrderBy(item => item.Identity, StringComparer.OrdinalIgnoreCase).ToArray();

    public BannerPreference ReadApplication(string identity)
    {
        var value = store.Read(ApplicationKey(identity), "ShowBanner");
        return new(identity, value is { Kind: PreferenceValueKind.DWord, Number: 0 },
            value is null || value is { Kind: PreferenceValueKind.DWord, Number: 0 or 1 });
    }

    public void SetHidden(string identity, bool hidden)
    {
        var path = ApplicationKey(identity);
        if (!store.SubKeys(SettingsKey).Contains(identity, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The notification sender is no longer registered.");
        if (!ReadApplication(identity).Available) throw new InvalidOperationException("Unsupported banner preference.");
        store.Write(path, "ShowBanner", new(PreferenceValueKind.DWord, Number: hidden ? 0 : 1));
        if (ReadApplication(identity).Hidden != hidden) throw new IOException("The banner preference was not saved.");
    }

    internal static bool IsApplicationIdentity(string identity) => !string.IsNullOrWhiteSpace(identity)
        && identity.Length <= 256 && identity.IndexOfAny(['\\', '/', '\0', '\r', '\n']) < 0
        && !identity.StartsWith("Windows.SystemToast.", StringComparison.OrdinalIgnoreCase)
        && !identity.StartsWith("Windows.ActionCenter.", StringComparison.OrdinalIgnoreCase);

    private static string ApplicationKey(string identity) => IsApplicationIdentity(identity)
        ? SettingsKey + "\\" + identity : throw new ArgumentException("Invalid notification sender.", nameof(identity));
}
