namespace WindowsIsland.Services;

internal sealed record PowerSnapshot(bool HasBattery, bool Connected, bool Charging, int? Percent);

internal sealed class PowerTracker
{
    private PowerSnapshot? _previous;
    private uint _sequence;

    public IslandNotification? Update(PowerSnapshot current)
    {
        var previous = _previous;
        _previous = current;
        if (!current.HasBattery || previous is null || !previous.HasBattery) return null;
        string? title = null;
        if (current.Connected != previous.Connected)
            title = current.Connected ? current.Charging ? "开始充电" : "已连接电源" : "已断开电源";
        else if (current.Connected && current.Percent == 100 && previous.Percent != 100)
            title = "电池已充满";
        else if (current.Connected && current.Charging && !previous.Charging)
            title = "开始充电";
        if (title is null) return null;
        return new(++_sequence, DateTimeOffset.UtcNow, "电源", title,
            current.Percent is { } percent ? $"电量 {percent}%" : "",
            Source: NotificationSource.Power, AppId: "power", Symbol: current.Connected && current.Charging ? "⚡" : "🔋");
    }
}
