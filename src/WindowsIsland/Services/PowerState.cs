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
            title = current.Connected ? current.Charging ? Localization.Get("PowerCharging") : Localization.Get("PowerConnected") : Localization.Get("PowerDisconnected");
        else if (current.Connected && current.Percent == 100 && previous.Percent != 100)
            title = Localization.Get("PowerFull");
        else if (current.Connected && current.Charging && !previous.Charging)
            title = Localization.Get("PowerCharging");
        if (title is null) return null;
        return new(++_sequence, DateTimeOffset.UtcNow, Localization.Get("Power"), title,
            current.Percent is { } percent ? Localization.Format("PowerBattery", percent) : "",
            Source: NotificationSource.Power, AppId: "power", Symbol: current.Connected && current.Charging ? "⚡" : "🔋");
    }
}
