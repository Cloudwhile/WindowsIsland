using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

// The contract is independent of WinUI. Built-ins share it with the host; external
// assembly loading and a public, versioned SDK are deliberately separate work.
internal interface INotificationPlugin : IDisposable
{
    event Action<IslandNotification>? Received;
    string StatusKey { get; }
    Task StartAsync();
    Task PollAsync();
    bool Allows(IslandNotification notification);
}

internal interface INotificationAccessPlugin
{
    NotificationAccess Access { get; }
    event Action<NotificationAccess>? AccessChanged;
    Task RequestAccessAsync();
}

// Optional: a source that can retry its own connection after reporting a failure.
internal interface INotificationRetryPlugin
{
    bool CanRetry { get; }
    void Retry();
}

internal sealed record NotificationPluginDefinition(string Id, string TitleKey, string ToggleId, int Icon,
    DiagnosticArea DiagnosticArea, Func<AppSettings, bool> IsEnabled,
    Func<AppSettings, bool, AppSettings> SetEnabled, Func<IslandNotification, bool> Matches);

internal sealed record NotificationPluginRegistration(NotificationPluginDefinition Definition, Func<INotificationPlugin> Create);
internal sealed record NotificationPluginStatus(NotificationPluginDefinition Definition, string StatusKey, bool CanRetry = false);
