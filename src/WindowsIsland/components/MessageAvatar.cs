using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class MessageAvatar : Grid
{
    private readonly AppIcon _portrait = new();
    private readonly PowerBatteryIcon _battery = new(AppIcon.IconSize, "") { Visibility = Visibility.Collapsed };

    public MessageAvatar()
    {
        Width = Height = AppIcon.IconSize;
        VerticalAlignment = VerticalAlignment.Top;
        Children.Add(_portrait);
        Children.Add(_battery);
    }

    public void Show(IslandNotification notification)
    {
        if (PowerEvents.TryResolveVisual(notification, out var state))
        {
            _portrait.Clear();
            _portrait.Visibility = Visibility.Collapsed;
            _portrait.SetAutomationId("");
            _battery.SetAutomationId("NotificationAppIcon");
            _battery.Visibility = Visibility.Visible;
            var appName = notification.Source == NotificationSource.Power ? Localization.Get("Power") : notification.AppName;
            _battery.Show(appName, state);
            return;
        }

        _battery.Clear();
        _battery.Visibility = Visibility.Collapsed;
        _battery.SetAutomationId("");
        _portrait.Visibility = Visibility.Visible;
        var hasAvatar = notification.SenderAvatar is { Length: > 0 };
        _portrait.SetCircular(hasAvatar);
        _portrait.SetAutomationId(hasAvatar ? "NotificationAvatar" : "NotificationAppIcon");
        _ = _portrait.ShowAsync(hasAvatar ? notification.Title : notification.AppName,
            hasAvatar ? notification.SenderAvatar : notification.AppIcon, hasAvatar ? null : notification.Symbol);
    }

    public void PlayPowerMotion(IslandMotionKind kind, int durationMs) => _battery.PlayMotion(kind, durationMs);
    public void StopPowerMotion() => _battery.StopMotion();

    public void Clear()
    {
        _portrait.Clear();
        _battery.Clear();
        _battery.Visibility = Visibility.Collapsed;
    }
}
