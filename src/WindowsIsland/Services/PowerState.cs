using System.Globalization;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;

namespace WindowsIsland.Services;

internal enum IslandMotionKind
{
    Default,
    PowerEngage,
    PowerPromote,
    PowerRelease,
    PowerAlert
}

internal enum PowerGlyphMode
{
    Charging,
    Connected,
    Disconnected,
    Full,
    LowBattery,
    ChargeLimit
}

internal readonly record struct PowerBatteryVisual(PowerGlyphMode Mode, int Percent, bool HasPercent);

internal readonly record struct PowerQuotaSettings(
    bool LowEnabled,
    int LowPercent,
    bool CriticalEnabled,
    int CriticalPercent,
    bool ChargeLimitEnabled,
    int ChargeLimitPercent)
{
    public const string LowEnabledKey = "lowEnabled";
    public const string LowPercentKey = "lowPercent";
    public const string CriticalEnabledKey = "criticalEnabled";
    public const string CriticalPercentKey = "criticalPercent";
    public const string ChargeLimitEnabledKey = "chargeLimitEnabled";
    public const string ChargeLimitPercentKey = "chargeLimitPercent";

    public const int DefaultLowPercent = 20;
    public const int MinLowPercent = 10;
    public const int MaxLowPercent = 50;

    public const int DefaultCriticalPercent = 10;
    public const int MinCriticalPercent = 5;
    public const int MaxCriticalPercent = 35;

    public const int DefaultChargeLimitPercent = 80;
    public const int MinChargeLimitPercent = 50;
    public const int MaxChargeLimitPercent = 100;
    public const int MaxConfigurableChargeLimitPercent = 95;

    public static PowerQuotaSettings Default => new(
        LowEnabled: true,
        LowPercent: DefaultLowPercent,
        CriticalEnabled: true,
        CriticalPercent: DefaultCriticalPercent,
        ChargeLimitEnabled: false,
        ChargeLimitPercent: DefaultChargeLimitPercent);

    public PowerQuotaSettings Normalized() => new(
        LowEnabled,
        Math.Clamp(LowPercent, MinLowPercent, MaxLowPercent),
        CriticalEnabled,
        Math.Clamp(CriticalPercent, MinCriticalPercent, MaxCriticalPercent),
        ChargeLimitEnabled,
        Math.Clamp(ChargeLimitPercent, MinChargeLimitPercent, MaxChargeLimitPercent));

    public (int Low, int Critical) EffectiveDischargeThresholds()
    {
        var low = Math.Clamp(LowPercent, MinLowPercent, MaxLowPercent);
        var critical = Math.Clamp(CriticalPercent, MinCriticalPercent, MaxCriticalPercent);
        if (LowEnabled && critical >= low)
            critical = Math.Max(MinCriticalPercent, low - 5);
        return (low, critical);
    }

    public static PowerQuotaSettings From(IPluginSettings? settings)
    {
        if (settings is null) return Default;
        return new PowerQuotaSettings(
            LowEnabled: settings.GetBoolean(LowEnabledKey, true),
            LowPercent: ParsePercent(settings.GetString(LowPercentKey), MinLowPercent, MaxLowPercent, DefaultLowPercent),
            CriticalEnabled: settings.GetBoolean(CriticalEnabledKey, true),
            CriticalPercent: ParsePercent(settings.GetString(CriticalPercentKey), MinCriticalPercent, MaxCriticalPercent, DefaultCriticalPercent),
            ChargeLimitEnabled: settings.GetBoolean(ChargeLimitEnabledKey, false),
            ChargeLimitPercent: ParsePercent(settings.GetString(ChargeLimitPercentKey), MinChargeLimitPercent, MaxChargeLimitPercent, DefaultChargeLimitPercent));
    }

    public static PowerQuotaSettings From(SettingsStore store) => new(
        LowEnabled: store.GetPluginBoolean(BuiltInPluginIds.Power, LowEnabledKey, true),
        LowPercent: ParsePercent(store.GetPluginString(BuiltInPluginIds.Power, LowPercentKey, null), MinLowPercent, MaxLowPercent, DefaultLowPercent),
        CriticalEnabled: store.GetPluginBoolean(BuiltInPluginIds.Power, CriticalEnabledKey, true),
        CriticalPercent: ParsePercent(store.GetPluginString(BuiltInPluginIds.Power, CriticalPercentKey, null), MinCriticalPercent, MaxCriticalPercent, DefaultCriticalPercent),
        ChargeLimitEnabled: store.GetPluginBoolean(BuiltInPluginIds.Power, ChargeLimitEnabledKey, false),
        ChargeLimitPercent: ParsePercent(store.GetPluginString(BuiltInPluginIds.Power, ChargeLimitPercentKey, null), MinChargeLimitPercent, MaxChargeLimitPercent, DefaultChargeLimitPercent));

    private static int ParsePercent(string? raw, int min, int max, int fallback) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= min && parsed <= max
            ? parsed : fallback;
}

internal static class PowerEvents
{
    public const string Charging = "power/charging";
    public const string Connected = "power/connected";
    public const string Disconnected = "power/disconnected";
    public const string Full = "power/full";
    public const string LowBattery = "power/low";
    public const string CriticalBattery = "power/critical";
    public const string ChargeLimit = "power/limit";
    public const string BatterySymbolPrefix = "battery:";

    public static string FormatSymbol(int? percent) =>
        percent is { } value ? $"{BatterySymbolPrefix}{Math.Clamp(value, 0, 100)}" : "battery";

    public static string FormatBody(string? eventId, int percent) => eventId switch
    {
        LowBattery => Localization.Format("PowerLowBatteryBody", percent),
        CriticalBattery => Localization.Format("PowerCriticalBatteryBody", percent),
        ChargeLimit => Localization.Format("PowerChargeLimitBody", percent),
        _ => Localization.Format("PowerBattery", percent)
    };

    public static IslandMotionKind ResolveMotion(IslandNotification? notification)
    {
        if (notification is null || notification.Source != NotificationSource.Power) return IslandMotionKind.Default;
        return notification.EventId switch
        {
            Disconnected => IslandMotionKind.PowerRelease,
            LowBattery or CriticalBattery => IslandMotionKind.PowerAlert,
            Charging or Connected or Full or ChargeLimit => IslandMotionKind.PowerEngage,
            _ => IslandMotionKind.Default
        };
    }

    public static IslandMotionKind ResolveTransitionMotion(IslandNotification? current, IslandNotification? previousVisible)
    {
        var baseKind = ResolveMotion(current);
        if (baseKind == IslandMotionKind.PowerEngage
            && previousVisible is { Source: NotificationSource.Power }
            && ResolveMotion(previousVisible) is IslandMotionKind.PowerEngage or IslandMotionKind.PowerPromote)
        {
            return IslandMotionKind.PowerPromote;
        }
        return baseKind;
    }

    public static bool TryResolveVisual(IslandNotification? notification, out PowerBatteryVisual visual)
    {
        visual = default;
        if (notification is null || notification.Source != NotificationSource.Power
            || notification.AppIcon is { Length: > 0 } || notification.SenderAvatar is { Length: > 0 })
            return false;

        var mode = notification.EventId switch
        {
            Charging => PowerGlyphMode.Charging,
            Disconnected => PowerGlyphMode.Disconnected,
            Full => PowerGlyphMode.Full,
            LowBattery or CriticalBattery => PowerGlyphMode.LowBattery,
            ChargeLimit => PowerGlyphMode.ChargeLimit,
            _ => PowerGlyphMode.Connected
        };

        if (TryParsePercent(notification.Symbol, notification.Body, out var percent))
        {
            visual = new PowerBatteryVisual(mode, percent, HasPercent: true);
            return true;
        }

        var fallback = mode switch
        {
            PowerGlyphMode.Full => 100,
            PowerGlyphMode.ChargeLimit => PowerQuotaSettings.DefaultChargeLimitPercent,
            PowerGlyphMode.LowBattery => PowerQuotaSettings.DefaultLowPercent,
            _ => 68
        };
        visual = new PowerBatteryVisual(mode, fallback, HasPercent: false);
        return true;
    }

    private static bool TryParsePercent(string? symbol, string? body, out int percent)
    {
        if (!string.IsNullOrWhiteSpace(symbol)
            && symbol.StartsWith(BatterySymbolPrefix, StringComparison.Ordinal)
            && int.TryParse(symbol.AsSpan(BatterySymbolPrefix.Length), out var tagged)
            && tagged is >= 0 and <= 100)
        {
            percent = tagged;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            var mark = body.IndexOf('%');
            if (mark > 0)
            {
                var start = mark - 1;
                while (start >= 0 && char.IsAsciiDigit(body[start])) start--;
                if (start + 1 < mark && int.TryParse(body.AsSpan(start + 1, mark - start - 1), out var parsed)
                    && parsed is >= 0 and <= 100)
                {
                    percent = parsed;
                    return true;
                }
            }
        }

        percent = 0;
        return false;
    }
}

internal sealed record PowerSnapshot(bool HasBattery, bool Connected, bool Charging, int? Percent);

internal sealed class PowerTracker
{
    public const int HysteresisMargin = 3;
    private PowerSnapshot? _previous;
    private PowerQuotaSettings _quota;
    private bool _percentSeeded;
    private bool _lowNotified, _criticalNotified, _chargeLimitNotified;
    private uint _sequence;

    public PowerTracker(PowerQuotaSettings? quota = null) =>
        _quota = (quota ?? PowerQuotaSettings.Default).Normalized();

    public PowerQuotaSettings Quota => _quota;

    public void ConfigureQuota(PowerQuotaSettings quota)
    {
        _quota = quota.Normalized();
        SyncFlagsWithCurrentState();
    }

    public IslandNotification? Update(PowerSnapshot current)
    {
        var previous = _previous;
        _previous = current;
        if (!current.HasBattery)
        {
            _percentSeeded = false;
            return null;
        }

        if (previous is null || !previous.HasBattery)
        {
            if (current.Percent is not null)
            {
                _percentSeeded = true;
                SyncFlagsWithCurrentState();
            }
            return null;
        }

        var hadPercentSeed = _percentSeeded;
        if (!hadPercentSeed && current.Percent is not null)
        {
            _percentSeeded = true;
            SyncFlagsWithCurrentState();
        }

        string? title = null, eventId = null;
        if (current.Connected != previous.Connected)
        {
            if (current.Connected)
            {
                title = current.Charging ? Localization.Get("PowerCharging") : Localization.Get("PowerConnected");
                eventId = current.Charging ? PowerEvents.Charging : PowerEvents.Connected;
            }
            else
            {
                title = Localization.Get("PowerDisconnected");
                eventId = PowerEvents.Disconnected;
            }
            if (_percentSeeded) SyncFlagsWithCurrentState();
        }
        else if (current.Connected)
        {
            if (current.Percent is { } cp && _chargeLimitNotified && cp <= _quota.ChargeLimitPercent - HysteresisMargin)
                _chargeLimitNotified = false;

            if (current.Percent == 100 && previous.Percent != 100)
            {
                _chargeLimitNotified = true;
                title = Localization.Get("PowerFull");
                eventId = PowerEvents.Full;
            }
            else if (current.Charging && !previous.Charging)
            {
                if (current.Percent is { } chgP && chgP >= _quota.ChargeLimitPercent)
                    _chargeLimitNotified = true;
                title = Localization.Get("PowerCharging");
                eventId = PowerEvents.Charging;
            }
            else if (hadPercentSeed && _quota.ChargeLimitEnabled && !_chargeLimitNotified
                && current.Percent is { } limitPercent && limitPercent >= _quota.ChargeLimitPercent)
            {
                _chargeLimitNotified = true;
                title = Localization.Get("PowerChargeLimitReached");
                eventId = PowerEvents.ChargeLimit;
            }
        }
        else
        {
            var (low, critical) = _quota.EffectiveDischargeThresholds();
            if (current.Percent is { } dp)
            {
                if (_lowNotified && dp >= low + HysteresisMargin) _lowNotified = false;
                if (_criticalNotified && dp >= critical + HysteresisMargin) _criticalNotified = false;
            }

            if (hadPercentSeed && _quota.CriticalEnabled && !_criticalNotified
                && current.Percent is { } critPercent && critPercent <= critical)
            {
                _criticalNotified = true;
                _lowNotified = true;
                title = Localization.Get("PowerCriticalBattery");
                eventId = PowerEvents.CriticalBattery;
            }
            else if (hadPercentSeed && _quota.LowEnabled && !_lowNotified
                && current.Percent is { } lowPercent && lowPercent <= low)
            {
                _lowNotified = true;
                title = Localization.Get("PowerLowBattery");
                eventId = PowerEvents.LowBattery;
            }
        }

        if (title is null) return null;
        return new(++_sequence, DateTimeOffset.UtcNow, Localization.Get("Power"), title,
            current.Percent is { } percent ? PowerEvents.FormatBody(eventId, percent) : "",
            Source: NotificationSource.Power, AppId: "power", EventId: eventId,
            Symbol: PowerEvents.FormatSymbol(current.Percent));
    }

    private void SyncFlagsWithCurrentState()
    {
        if (_previous is not { HasBattery: true, Percent: { } p } current) return;
        var (low, critical) = _quota.EffectiveDischargeThresholds();
        _lowNotified = !_quota.LowEnabled || current.Connected || p <= low;
        _criticalNotified = !_quota.CriticalEnabled || current.Connected || p <= critical;
        _chargeLimitNotified = !_quota.ChargeLimitEnabled || !current.Connected || p >= _quota.ChargeLimitPercent;
    }
}
