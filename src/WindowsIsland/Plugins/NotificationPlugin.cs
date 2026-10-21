using WindowsIsland.Services;
using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

internal sealed record NotificationPluginDefinition(string Id, string TitleKey, string ToggleId, int Icon,
    DiagnosticArea DiagnosticArea, Func<AppSettings, bool> IsEnabled,
    Func<AppSettings, bool, AppSettings> SetEnabled, Func<IslandNotification, bool> Matches,
    PluginMetadata? Metadata = null, string RetryLabelKey = "ActionRetrySource")
{
    /// <summary>判断该注册项能否删除，内置标记及保留标识均受保护。</summary>
    public bool CanRemove => Metadata is { IsBuiltIn: false } && !BuiltInPluginIds.IsReserved(Id);
}

internal sealed record NotificationPluginRegistration(NotificationPluginDefinition Definition, Func<IMessageSource> Create);
internal sealed record NotificationPluginStatus(NotificationPluginDefinition Definition, string StatusKey, bool CanRetry = false,
    PluginStatus? Connection = null);
