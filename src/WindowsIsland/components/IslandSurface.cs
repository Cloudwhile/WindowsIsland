using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WindowsIsland.Components;

internal sealed class IslandSurface : FlyoutPresenter
{
    public IslandSurface(UIElement content)
    {
        Content = content;
        IsTabStop = false;
        AllowFocusOnInteraction = false;
        UseLayoutRounding = true;
        MaxWidth = NotificationLayout.MaxWidth;
        MaxHeight = NotificationLayout.MaxHeight;
        CornerRadius = new CornerRadius(NotificationLayout.CornerRadius);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollMode(this, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollMode(this, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(this, ScrollBarVisibility.Disabled);
    }
}
