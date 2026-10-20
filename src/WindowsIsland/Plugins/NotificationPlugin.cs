using WindowsIsland.Services;
using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

internal sealed record NotificationPluginDefinition(string Id, string TitleKey, string ToggleId, int Icon,
    DiagnosticArea DiagnosticArea, Func<AppSettings, bool> IsEnabled,
    Func<AppSettings, bool, AppSettings> SetEnabled, Func<IslandNotification, bool> Matches);

internal sealed record NotificationPluginRegistration(NotificationPluginDefinition Definition, Func<IMessageSource> Create);
internal sealed record NotificationPluginStatus(NotificationPluginDefinition Definition, string StatusKey, bool CanRetry = false,
    PluginStatus? Connection = null);
