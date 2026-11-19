using System.Numerics;

namespace WindowsIsland.Services;

// 充电接入与断电回弹的纯几何参数，覆盖八方位锚点与边界夹紧。
internal static class PowerMotionProfile
{
    public const int EngageOpenDuration = 880, EngageUpdateDuration = 760;
    public const int ReleaseOpenDuration = 860, ReleaseUpdateDuration = 760;
    public const int AuraEngageDuration = 1850, AuraReleaseDuration = 1750;

    public static Vector2 Inward(NotificationPosition position)
    {
        var raw = -NotificationPlacement.Outward(position);
        return raw.LengthSquared() > 0.001f ? Vector2.Normalize(raw) : new Vector2(0, 1);
    }

    public static Vector2 Lateral(NotificationPosition position)
    {
        var inward = Inward(position);
        return new Vector2(-inward.Y, inward.X);
    }

    public static Vector2 IgnitionLine(NotificationPosition position, Vector2 target) =>
        NotificationPlacement.IsSide(position)
            ? new(4.5f, Math.Clamp(target.Y * 0.72f, 72f, Math.Max(72f, target.Y)))
            : new(Math.Clamp(target.X * 0.66f, 72f, Math.Max(72f, target.X)), 4.5f);

    public static Vector2 Deform(NotificationPosition position, Vector2 target, float tangentScale, float normalScale,
        float maxWidth = 480f, float maxHeight = 218f)
    {
        var side = NotificationPlacement.IsSide(position);
        var rawWidth = target.X * (side ? normalScale : tangentScale);
        var rawHeight = target.Y * (side ? tangentScale : normalScale);
        var widthLimit = Math.Max(target.X, Math.Min(maxWidth, target.X + 54f));
        var heightLimit = Math.Max(target.Y, Math.Min(maxHeight, target.Y + 26f));
        return new Vector2(
            Math.Clamp(rawWidth, 12f, widthLimit),
            Math.Clamp(rawHeight, 12f, heightLimit));
    }

    // 圆角矩形路径按顶边左起顺时针度量；返回锚点对侧的 (水平直边, 圆角弧段, 垂直直边) 权重，使 Trim 0.5 精确落在屏幕锚点。
    public static Vector3 OppositePerimeterWeights(NotificationPosition position) =>
        NotificationPlacement.Normalize(position) switch
        {
            NotificationPosition.TopLeft => new(1.0f, 1.5f, 1.0f),
            NotificationPosition.LeftCenter => new(1.0f, 1.0f, 0.5f),
            NotificationPosition.BottomLeft => new(1.0f, 0.5f, 0.0f),
            NotificationPosition.BottomCenter => new(0.5f, 0.0f, 0.0f),
            NotificationPosition.BottomRight => new(2.0f, 3.5f, 2.0f),
            NotificationPosition.RightCenter => new(2.0f, 3.0f, 1.5f),
            NotificationPosition.TopRight => new(2.0f, 2.5f, 1.0f),
            _ => new(1.5f, 2.0f, 1.0f)
        };

    public static float OppositePerimeterOffset(NotificationPosition position, Vector2 size, float maxCornerRadius = 27.5f)
    {
        var width = Math.Max(1f, size.X - 1f);
        var height = Math.Max(1f, size.Y - 1f);
        var radius = Math.Min(maxCornerRadius, Math.Min(width, height) * 0.5f);
        var horizontal = Math.Max(0f, width - 2f * radius);
        var vertical = Math.Max(0f, height - 2f * radius);
        var corner = MathF.PI * 0.5f * radius;
        var perimeter = Math.Max(1f, 2f * horizontal + 2f * vertical + 4f * corner);
        var weights = OppositePerimeterWeights(position);
        return Math.Clamp((weights.X * horizontal + weights.Y * corner + weights.Z * vertical) / perimeter, 0f, 1f);
    }
}
