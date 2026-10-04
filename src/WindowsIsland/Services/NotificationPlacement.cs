using System.Numerics;

namespace WindowsIsland.Services;

internal enum NotificationPosition { TopCenter, TopLeft, LeftCenter, BottomLeft, BottomCenter, BottomRight, RightCenter, TopRight }

internal static class NotificationPlacement
{
    public static NotificationPosition Normalize(NotificationPosition position) => Enum.IsDefined(position) ? position : NotificationPosition.TopCenter;

    public static Vector2 Anchor(NotificationPosition position) => Normalize(position) switch
    {
        NotificationPosition.TopLeft => new(0, 0),
        NotificationPosition.LeftCenter => new(0, 0.5f),
        NotificationPosition.BottomLeft => new(0, 1),
        NotificationPosition.BottomCenter => new(0.5f, 1),
        NotificationPosition.BottomRight => new(1, 1),
        NotificationPosition.RightCenter => new(1, 0.5f),
        NotificationPosition.TopRight => new(1, 0),
        _ => new(0.5f, 0)
    };

    public static string Label(NotificationPosition position) => Normalize(position) switch
    {
        NotificationPosition.TopLeft => "左上",
        NotificationPosition.LeftCenter => "左中",
        NotificationPosition.BottomLeft => "左下",
        NotificationPosition.BottomCenter => "中下",
        NotificationPosition.BottomRight => "右下",
        NotificationPosition.RightCenter => "右中",
        NotificationPosition.TopRight => "右上",
        _ => "中上"
    };

    public static Vector2 Offset(NotificationPosition position, Vector2 container, Vector2 content, float inset = 0)
    {
        var remaining = Vector2.Max(Vector2.Zero, container - content - new Vector2(inset * 2));
        return new Vector2(inset) + remaining * Anchor(position);
    }

    public static Vector2 Outward(NotificationPosition position) => Anchor(position) * 2 - Vector2.One;
    public static Vector2 EdgeTranslation(NotificationPosition position, float inset) => Outward(position) * inset;
    public static bool IsSide(NotificationPosition position) => position is NotificationPosition.LeftCenter or NotificationPosition.RightCenter;
    public static Vector2 LineSize(NotificationPosition position) => IsSide(position) ? new(3, 72) : new(72, 3);
    public static Vector2 Shoulder(NotificationPosition position, Vector2 target) => IsSide(position)
        ? new(6, Math.Min(144, target.Y)) : new(Math.Min(144, target.X), 6);
}
