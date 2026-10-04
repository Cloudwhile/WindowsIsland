using System.Numerics;
using WindowsIsland.Services;

internal static class PositionChecks
{
    public static void Run(Action<bool, string> check)
    {
        var screen = new Vector2(1920, 1040);
        var host = new Vector2(504, 242);
        var pill = new Vector2(300, 135);
        var expected = new (NotificationPosition Position, Vector2 Origin, Vector2 Outward)[]
        {
            (NotificationPosition.TopLeft, new(12, 12), new(-1, -1)),
            (NotificationPosition.LeftCenter, new(12, 452.5f), new(-1, 0)),
            (NotificationPosition.BottomLeft, new(12, 893), new(-1, 1)),
            (NotificationPosition.BottomCenter, new(810, 893), new(0, 1)),
            (NotificationPosition.BottomRight, new(1608, 893), new(1, 1)),
            (NotificationPosition.RightCenter, new(1608, 452.5f), new(1, 0)),
            (NotificationPosition.TopRight, new(1608, 12), new(1, -1)),
            (NotificationPosition.TopCenter, new(810, 12), new(0, -1))
        };
        foreach (var (position, origin, outward) in expected)
        {
            var window = NotificationPlacement.Offset(position, screen, host);
            var content = NotificationPlacement.Offset(position, host, pill, 12);
            check(window + content == origin, $"{position} aligns its visible pill and input region to the requested screen position");
            check(NotificationPlacement.Outward(position) == outward, $"{position} opens inward and closes toward its own edge");
            var line = NotificationPlacement.LineSize(position);
            check(NotificationPlacement.IsSide(position) ? line.X == 3 && line.Y == 72 : line.X == 72 && line.Y == 3,
                $"{position} uses a thin line parallel to its screen edge");
            var large = new Vector2(480, 218);
            var resized = window + NotificationPlacement.Offset(position, host, large, 12);
            var anchor = NotificationPlacement.Anchor(position);
            var collapsed = window + NotificationPlacement.Offset(position, host, line, 12)
                + NotificationPlacement.EdgeTranslation(position, 12);
            var edge = collapsed + line * anchor;
            check(Vector2.Distance(edge, screen * anchor) < 0.01f,
                $"{position} collapses completely into a line touching the corresponding work-area edge");
            check(resized + large * anchor == origin + pill * anchor,
                $"{position} preserves its anchor when notification content grows");
        }
        check(NotificationPlacement.Offset(NotificationPosition.BottomRight, new(200, 100), new(176, 76), 12) == new Vector2(12),
            "Notifications clamped to a small work area retain the outer margin");
        check(NotificationPlacement.Label(NotificationPosition.TopCenter) == "中上"
            && NotificationPlacement.Label(NotificationPosition.BottomCenter) == "中下", "Position labels follow the eight-direction setting");
    }
}
