using Microsoft.UI.Xaml;

namespace WindowsIsland.Components;

internal sealed record SettingsDetail(string Title, bool LocalizeTitle, FrameworkElement Content,
    Action? Refresh = null, Action? Close = null, Func<bool>? IsCurrent = null);
