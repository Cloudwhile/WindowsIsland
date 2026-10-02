using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class MessageAvatar : Grid
{
    private readonly AppIcon _portrait = new();

    public MessageAvatar()
    {
        Width = Height = AppIcon.IconSize;
        VerticalAlignment = VerticalAlignment.Top;
        Children.Add(_portrait);
    }

    public void Show(IslandNotification notification)
    {
        var hasAvatar = notification.SenderAvatar is { Length: > 0 };
        _portrait.SetCircular(hasAvatar);
        _portrait.SetAutomationId(hasAvatar ? "NotificationAvatar" : "NotificationAppIcon");
        _ = _portrait.ShowAsync(hasAvatar ? notification.Title : notification.AppName,
            hasAvatar ? notification.SenderAvatar : notification.AppIcon, hasAvatar ? null : notification.Symbol);
    }

    public void Clear()
    {
        _portrait.Clear();
    }
}
