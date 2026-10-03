using System.Text.Json;

namespace WindowsIsland.Services;

internal sealed record AppSettings(bool SetupCompleted = false, bool SystemNotifications = true,
    bool WeChat = true, bool Telegram = true, bool Power = true, bool Animations = true)
{
    public bool AllowsClient(string identity) => identity switch
    {
        "wechat" => WeChat,
        "telegram" => Telegram,
        _ => false
    };

    public bool Allows(IslandNotification notification)
    {
        if (notification.Source == NotificationSource.Power) return Power;
        var identity = MessengerIdentity.FromNotification(notification);
        if (identity is "wechat" or "telegram" && !AllowsClient(identity)) return false;
        return notification.Source != NotificationSource.SystemNotification || SystemNotifications;
    }
}

internal sealed class SettingsStore
{
    private readonly string _path;
    private AppSettings _current;
    public AppSettings Current => Volatile.Read(ref _current);
    public event Action<AppSettings>? Changed;

    public SettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowsIsland", "settings.json");
        try { _current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { _current = new(); }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, overwrite: true);
        Volatile.Write(ref _current, settings);
        Changed?.Invoke(settings);
    }
}
