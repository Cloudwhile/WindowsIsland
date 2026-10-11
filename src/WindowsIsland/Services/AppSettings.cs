using System.Text.Json;
using System.Text.Json.Nodes;

namespace WindowsIsland.Services;

internal sealed record AppSettings(bool SetupCompleted = false, bool SystemNotifications = true,
    bool WeChat = true, bool Telegram = true, bool Power = true, bool Animations = true,
    NotificationPosition Position = NotificationPosition.TopCenter, bool IncludePrereleaseUpdates = false,
    string Language = LanguagePreferences.Default, bool MuteDuringGames = true, bool MuteDuringFullScreen = true,
    bool LocalDiagnostics = true)
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
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(_path));
            if (json is JsonObject oldPreferences && !oldPreferences.ContainsKey(nameof(AppSettings.MuteDuringFullScreen)))
            {
                var oldMute = oldPreferences[nameof(AppSettings.MuteDuringGames)];
                oldPreferences[nameof(AppSettings.MuteDuringFullScreen)] = oldMute is JsonValue oldValue
                    && oldValue.TryGetValue<bool>(out var enabled) ? enabled : true;
            }
            if (json is JsonObject preferences)
            {
                foreach (var name in new[] { nameof(AppSettings.SetupCompleted), nameof(AppSettings.SystemNotifications),
                    nameof(AppSettings.WeChat), nameof(AppSettings.Telegram), nameof(AppSettings.Power), nameof(AppSettings.Animations),
                    nameof(AppSettings.IncludePrereleaseUpdates), nameof(AppSettings.MuteDuringGames), nameof(AppSettings.MuteDuringFullScreen),
                    nameof(AppSettings.LocalDiagnostics) })
                    if (preferences.TryGetPropertyValue(name, out var node)
                        && (node is not JsonValue value || !value.TryGetValue<bool>(out _))) preferences.Remove(name);
                if (preferences.TryGetPropertyValue(nameof(AppSettings.Language), out var language)
                    && (language is not JsonValue languageValue || !languageValue.TryGetValue<string>(out _)))
                    preferences.Remove(nameof(AppSettings.Language));
                if (preferences.TryGetPropertyValue(nameof(AppSettings.Position), out var position)
                    && (position is not JsonValue positionValue || !positionValue.TryGetValue<int>(out _)))
                    preferences.Remove(nameof(AppSettings.Position));
            }
            _current = json?.Deserialize<AppSettings>() ?? new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            if (error is not (FileNotFoundException or DirectoryNotFoundException))
                Diagnostics.Capture(DiagnosticArea.Settings, error, "ReadPreferences");
            _current = new();
        }
        _current = _current with { Position = NotificationPlacement.Normalize(_current.Position), Language = LanguagePreferences.Normalize(_current.Language) };
    }

    public void Save(AppSettings settings)
    {
        settings = settings with { Position = NotificationPlacement.Normalize(settings.Position), Language = LanguagePreferences.Normalize(settings.Language) };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, overwrite: true);
        Volatile.Write(ref _current, settings);
        Changed?.Invoke(settings);
    }
}
