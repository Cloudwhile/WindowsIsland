using System.Numerics;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

internal static class PowerMotionChecks
{
    public static void Run(Action<bool, string> check)
    {
        var tracker = new PowerTracker();
        tracker.Update(new(true, false, false, 64));
        var charging = tracker.Update(new(true, true, true, 64))!;
        var full = tracker.Update(new(true, true, false, 100))!;
        var unplugged = tracker.Update(new(true, false, false, 100))!;
        var connected = tracker.Update(new(true, true, false, 99))!;
        check(charging.EventId == PowerEvents.Charging && PowerEvents.ResolveMotion(charging) == IslandMotionKind.PowerEngage,
            "Charging notifications carry a stable engage motion identity");
        check(connected.EventId == PowerEvents.Connected && PowerEvents.ResolveMotion(connected) == IslandMotionKind.PowerEngage,
            "External power connection uses the engage window response");
        check(full.EventId == PowerEvents.Full && PowerEvents.ResolveMotion(full) == IslandMotionKind.PowerEngage,
            "Full-charge notifications use the engage window response");
        check(unplugged.EventId == PowerEvents.Disconnected && PowerEvents.ResolveMotion(unplugged) == IslandMotionKind.PowerRelease,
            "Power disconnection notifications use the release window response");
        check(new[] { charging, connected, full, unplugged }.All(item =>
            item.Symbol is not null && item.Symbol.StartsWith(PowerEvents.BatterySymbolPrefix, StringComparison.Ordinal)
            && !item.Symbol.Contains("⚡", StringComparison.Ordinal)
            && !item.Symbol.Contains("🔋", StringComparison.Ordinal)),
            "Power notifications emit structured battery tokens instead of emoji");

        var bridgedEngage = PluginNotificationBridge.ToHost(PluginNotificationBridge.ToContract(BuiltInPluginIds.Power, charging));
        var bridgedRelease = PluginNotificationBridge.ToHost(PluginNotificationBridge.ToContract(BuiltInPluginIds.Power, unplugged));
        var bridgedFull = PluginNotificationBridge.ToHost(PluginNotificationBridge.ToContract(BuiltInPluginIds.Power, full));
        var bridgedConnected = PluginNotificationBridge.ToHost(PluginNotificationBridge.ToContract(BuiltInPluginIds.Power, connected));
        check(PowerEvents.ResolveMotion(bridgedEngage) == IslandMotionKind.PowerEngage
            && PowerEvents.ResolveMotion(bridgedRelease) == IslandMotionKind.PowerRelease,
            "Power motion identities survive contract serialization across the plugin bridge");
        check(PowerEvents.TryResolveVisual(bridgedEngage, out var engageVisual)
            && engageVisual is { Mode: PowerGlyphMode.Charging, Percent: 64, HasPercent: true }
            && PowerEvents.TryResolveVisual(bridgedFull, out var fullVisual)
            && fullVisual is { Mode: PowerGlyphMode.Full, Percent: 100, HasPercent: true }
            && PowerEvents.TryResolveVisual(bridgedConnected, out var connectedVisual)
            && connectedVisual is { Mode: PowerGlyphMode.Connected, Percent: 99, HasPercent: true }
            && PowerEvents.TryResolveVisual(bridgedRelease, out var releaseVisual)
            && releaseVisual is { Mode: PowerGlyphMode.Disconnected, Percent: 100, HasPercent: true },
            "Vector battery visual resolver preserves mode and exact battery percentage across the plugin bridge");
        check(!PowerEvents.TryResolveVisual(bridgedEngage with { AppIcon = [1, 2, 3] }, out _),
            "Explicit custom PNG artwork opts out of the built-in vector battery icon");

        var nonPower = charging with { Source = NotificationSource.SystemNotification, AppName = "Mail", AppId = "mail" };
        check(PowerEvents.ResolveMotion(null) == IslandMotionKind.Default
            && PowerEvents.ResolveMotion(nonPower) == IslandMotionKind.Default,
            "Non-power notifications never trigger power window animations even with matching event strings");
        check(PowerEvents.ResolveTransitionMotion(charging, null) == IslandMotionKind.PowerEngage
            && PowerEvents.ResolveTransitionMotion(charging, unplugged) == IslandMotionKind.PowerEngage
            && PowerEvents.ResolveTransitionMotion(charging, nonPower) == IslandMotionKind.PowerEngage
            && PowerEvents.ResolveTransitionMotion(charging, connected) == IslandMotionKind.PowerPromote
            && PowerEvents.ResolveTransitionMotion(full, charging) == IslandMotionKind.PowerPromote
            && PowerEvents.ResolveTransitionMotion(unplugged, charging) == IslandMotionKind.PowerRelease,
            "In-place power engage upgrades promote smoothly without double-bouncing while cold opens and unplugs retain crisp motion");

        var pill = new Vector2(300, 96);
        var maxPill = new Vector2(480, 218);
        foreach (var position in Enum.GetValues<NotificationPosition>())
        {
            var inward = PowerMotionProfile.Inward(position);
            var lateral = PowerMotionProfile.Lateral(position);
            var outward = NotificationPlacement.Outward(position);
            check(MathF.Abs(inward.Length() - 1f) < 0.001f && Vector2.Dot(inward, outward) < -0.7f,
                $"{position} inward vector is unit-length and opposes its screen edge");
            check(MathF.Abs(lateral.Length() - 1f) < 0.001f && MathF.Abs(Vector2.Dot(inward, lateral)) < 0.001f,
                $"{position} lateral jolt vector is unit-length and orthogonal to inward motion");

            var engage = PowerMotionProfile.Deform(position, pill, 1.072f, 1.036f);
            var release = PowerMotionProfile.Deform(position, pill, 0.932f, 1.046f);
            var clamped = PowerMotionProfile.Deform(position, maxPill, 1.072f, 1.036f);
            var ignition = PowerMotionProfile.IgnitionLine(position, pill);
            if (NotificationPlacement.IsSide(position))
            {
                check(engage.Y > pill.Y && engage.X > pill.X && release.Y < pill.Y && release.X > pill.X,
                    $"{position} maps charge expansion and unplug narrowing to the vertical edge axis");
                check(MathF.Abs(ignition.X - 4.5f) < 0.001f && ignition.Y >= 72f,
                    $"{position} widens the magnetic contact strip vertically along its side edge");
            }
            else
            {
                check(engage.X > pill.X && engage.Y > pill.Y && release.X < pill.X && release.Y > pill.Y,
                    $"{position} maps charge expansion and unplug narrowing to the horizontal edge axis");
                check(ignition.X >= 72f && MathF.Abs(ignition.Y - 4.5f) < 0.001f,
                    $"{position} widens the magnetic contact strip horizontally along its screen edge");
            }
            check(clamped.X <= 480f && clamped.Y <= 218f,
                $"{position} clamps power overshoot within host window bounds");

            foreach (var sample in new[] { NotificationPlacement.LineSize(position), new Vector2(144, 6), pill, maxPill })
            {
                var offset = PowerMotionProfile.OppositePerimeterOffset(position, sample);
                check(offset is >= 0f and <= 1f, $"{position} perimeter trim offset stays normalized for {sample}");
            }
        }

        var antipodes = new (NotificationPosition First, NotificationPosition Second)[]
        {
            (NotificationPosition.TopCenter, NotificationPosition.BottomCenter),
            (NotificationPosition.LeftCenter, NotificationPosition.RightCenter),
            (NotificationPosition.TopLeft, NotificationPosition.BottomRight),
            (NotificationPosition.TopRight, NotificationPosition.BottomLeft)
        };
        foreach (var (first, second) in antipodes)
        {
            var delta = MathF.Abs(PowerMotionProfile.OppositePerimeterOffset(first, pill)
                - PowerMotionProfile.OppositePerimeterOffset(second, pill));
            check(MathF.Abs(delta - 0.5f) < 0.0005f,
                $"{first} and {second} place their perimeter seams on exact opposite halves of the capsule");
        }

        RunQuotaChecks(check);
    }

    private static void RunQuotaChecks(Action<bool, string> check)
    {
        Localization.Configure("zh-CN");

        // 1. Startup silent baseline when already below low threshold (18%), still armed for critical (10%)
        var startupLow = new PowerTracker();
        check(startupLow.Update(new(true, false, false, 18)) is null,
            "Startup below low-battery threshold establishes a silent baseline without spamming");
        check(startupLow.Update(new(true, false, false, 17)) is null,
            "Continuing below an already-crossed low threshold does not emit a retroactive low alert");
        var hitCriticalFromBaseline = startupLow.Update(new(true, false, false, 10))!;
        check(hitCriticalFromBaseline.EventId == PowerEvents.CriticalBattery
            && hitCriticalFromBaseline.Title == "电量严重不足"
            && PowerEvents.ResolveMotion(hitCriticalFromBaseline) == IslandMotionKind.PowerAlert
            && PowerEvents.TryResolveVisual(hitCriticalFromBaseline, out var critVisual)
            && critVisual is { Mode: PowerGlyphMode.LowBattery, Percent: 10, HasPercent: true },
            "Critical battery alert still fires when discharging crosses the critical threshold after a low startup baseline");

        // 2. Discharging across Low (20%) & Critical (10%) with +/-3% anti-jitter hysteresis
        var discharge = new PowerTracker();
        check(discharge.Update(new(true, false, false, 24)) is null
            && discharge.Update(new(true, false, false, 21)) is null,
            "Discharging above the low-battery threshold stays silent");
        var lowAlert = discharge.Update(new(true, false, false, 20))!;
        check(lowAlert.EventId == PowerEvents.LowBattery && lowAlert.Title == "电量偏低"
            && lowAlert.Body.Contains("20%", StringComparison.Ordinal)
            && PowerEvents.ResolveMotion(lowAlert) == IslandMotionKind.PowerAlert,
            "Crossing the low-battery threshold emits a single PowerAlert notification");
        check(discharge.Update(new(true, false, false, 21)) is null
            && discharge.Update(new(true, false, false, 20)) is null
            && discharge.Update(new(true, false, false, 19)) is null,
            "1-2% gauge jitter within hysteresis margin does not repeat the low-battery popup");
        check(discharge.Update(new(true, false, false, 23)) is null
            && discharge.Update(new(true, false, false, 20))?.EventId == PowerEvents.LowBattery,
            "Recovering at or beyond the hysteresis margin re-arms the low-battery reminder");
        var critAlert = discharge.Update(new(true, false, false, 10))!;
        check(critAlert.EventId == PowerEvents.CriticalBattery
            && discharge.Update(new(true, false, false, 9)) is null,
            "Crossing the critical threshold emits one urgent alert without repeating on subsequent 1% drops");

        // 3. Sleep/resume jump straight across both thresholds emits only Critical, not Low afterward
        var sleepJump = new PowerTracker();
        sleepJump.Update(new(true, false, false, 35));
        var jumped = sleepJump.Update(new(true, false, false, 8))!;
        check(jumped.EventId == PowerEvents.CriticalBattery
            && sleepJump.Update(new(true, false, false, 7)) is null,
            "Deep sleep battery drop across both thresholds emits one critical alert and suppresses redundant low alert");

        // 4. Charge limit (80%) crossing, EC charge-stop tolerance, runtime re-config & 100% deduplication
        var limitTracker = new PowerTracker(PowerQuotaSettings.Default with { ChargeLimitEnabled = true, ChargeLimitPercent = 80 });
        limitTracker.Update(new(true, true, true, 76));
        check(limitTracker.Update(new(true, true, true, 79)) is null,
            "Charging below the configured charge limit stays silent");
        var limitReached = limitTracker.Update(new(true, true, true, 80))!;
        var bridgedLimit = PluginNotificationBridge.ToHost(PluginNotificationBridge.ToContract(BuiltInPluginIds.Power, limitReached));
        check(bridgedLimit.EventId == PowerEvents.ChargeLimit
            && bridgedLimit.Title == "已达充电上限"
            && PowerEvents.ResolveMotion(bridgedLimit) == IslandMotionKind.PowerEngage
            && PowerEvents.TryResolveVisual(bridgedLimit, out var limitVisual)
            && limitVisual is { Mode: PowerGlyphMode.ChargeLimit, Percent: 80, HasPercent: true },
            "Reaching the charge limit emits an engage notification with ChargeLimit glyph");
        check(limitTracker.Update(new(true, true, true, 81)) is null
            && limitTracker.Update(new(true, true, false, 100))?.EventId == PowerEvents.Full,
            "Charge limit alert fires once and still allows the 100% full-charge event");

        var ecStopLimit = new PowerTracker(PowerQuotaSettings.Default with { ChargeLimitEnabled = true, ChargeLimitPercent = 80 });
        ecStopLimit.Update(new(true, true, true, 79));
        check(ecStopLimit.Update(new(true, true, false, 80))?.EventId == PowerEvents.ChargeLimit
            && ecStopLimit.Update(new(true, true, false, 80)) is null,
            "Charge limit still triggers cleanly if laptop EC stops active charging on the exact threshold poll");

        var fullLimit = new PowerTracker(PowerQuotaSettings.Default with { ChargeLimitEnabled = true, ChargeLimitPercent = 100 });
        fullLimit.Update(new(true, true, true, 99));
        check(fullLimit.Update(new(true, true, false, 100))?.EventId == PowerEvents.Full,
            "A 100% charge limit does not shadow or duplicate the built-in full-battery notification");

        // Runtime slider adjustment does not spam immediate popups
        var liveTracker = new PowerTracker();
        liveTracker.Update(new(true, false, false, 32));
        liveTracker.ConfigureQuota(PowerQuotaSettings.Default with { LowEnabled = true, LowPercent = 40 });
        check(liveTracker.Update(new(true, false, false, 31)) is null,
            "Raising low threshold above current battery level in settings re-seeds baseline without instant spam");
        liveTracker.ConfigureQuota(PowerQuotaSettings.Default with { LowEnabled = true, LowPercent = 28 });
        check(liveTracker.Update(new(true, false, false, 28))?.EventId == PowerEvents.LowBattery,
            "Lowering low threshold below current battery level arms the new target immediately");

        // 5. Per-field fault tolerance & unrelated preference preservation
        var tempRoot = Path.Combine(Path.GetTempPath(), "wini-power-quota-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var jsonPath = Path.Combine(tempRoot, "settings.json");
            File.WriteAllText(jsonPath, """
                {
                  "Power": true,
                  "Plugins": {
                    "wini_plugin_power": {
                      "mode": "保留",
                      "lowEnabled": true,
                      "lowPercent": "corrupted",
                      "criticalEnabled": "not-bool",
                      "criticalPercent": "15",
                      "chargeLimitEnabled": true,
                      "chargeLimitPercent": "999"
                    }
                  }
                }
                """);
            var store = new SettingsStore(jsonPath);
            var parsed = PowerQuotaSettings.From(store);
            check(parsed.LowEnabled
                && parsed.LowPercent == PowerQuotaSettings.DefaultLowPercent
                && parsed.CriticalEnabled
                && parsed.CriticalPercent == 15
                && parsed.ChargeLimitEnabled
                && parsed.ChargeLimitPercent == PowerQuotaSettings.DefaultChargeLimitPercent
                && store.GetPluginString(BuiltInPluginIds.Power, "mode", null) == "保留",
                "Corrupted or out-of-range quota fields fall back individually without resetting valid sibling preferences");

            var inverted = (PowerQuotaSettings.Default with { LowEnabled = true, LowPercent = 15, CriticalEnabled = true, CriticalPercent = 25 })
                .EffectiveDischargeThresholds();
            check(inverted.Low == 15 && inverted.Critical == 10,
                "Effective discharge thresholds clamp critical strictly below low when both are enabled");

            // 6. Live hot-reload through PluginManager -> PowerPlugin -> IPowerQuotaBackend
            QuotaBackendProbe? probe = null;
            using var manager = new PluginManager(
                [new(BuiltInPluginDefinitions.Power, () => new PowerPlugin())],
                store,
                action => action(),
                id => id == BuiltInPluginIds.Power ? (probe = new QuotaBackendProbe()) : null);
            manager.StartAsync().GetAwaiter().GetResult();
            check(probe is not null && probe.Latest.CriticalPercent == 15 && probe.Latest.ChargeLimitEnabled,
                "PowerPlugin forwards stored quota settings to its backend before initial StartAsync");
            var model = manager.GetSettingsAsync(BuiltInPluginIds.Power).GetAwaiter().GetResult()!;
            model.Settings.SetString(PowerQuotaSettings.ChargeLimitPercentKey, "85");
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (probe!.Updates < 2 && DateTime.UtcNow < deadline) Thread.Sleep(15);
            check(probe.Latest.ChargeLimitPercent == 85 && store.GetPluginString(BuiltInPluginIds.Power, "mode", null) == "保留",
                "Editing power quota settings via IPluginSettings hot-reloads the active backend and preserves unrelated keys");

            // 7. English translation parity for quota notifications
            Localization.Configure("en-US");
            var enTracker = new PowerTracker(PowerQuotaSettings.Default with { ChargeLimitEnabled = true, ChargeLimitPercent = 80 });
            enTracker.Update(new(true, true, true, 78));
            var enLimit = enTracker.Update(new(true, true, true, 80))!;
            check(enLimit.Title == "Charge limit reached" && enLimit.Body.Contains("80%", StringComparison.Ordinal),
                "Quota notifications format cleanly in English");
        }
        finally
        {
            Localization.Configure("zh-CN");
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private sealed class QuotaBackendProbe : WindowsIsland.Contracts.IMessageSourceBackend, IPowerQuotaBackend
    {
        public event Action<WindowsIsland.Contracts.PluginNotification>? NotificationReceived { add { } remove { } }
        public WindowsIsland.Contracts.PluginStatus Status => new(WindowsIsland.Contracts.PluginConnectionState.Connected);
        public PowerQuotaSettings Latest { get; private set; } = PowerQuotaSettings.Default;
        public int Updates { get; private set; }
        public void ConfigureQuota(PowerQuotaSettings quota) { Latest = quota; Updates++; }
        public Task StartAsync() => Task.CompletedTask;
        public Task PollAsync() => Task.CompletedTask;
        public bool Allows(WindowsIsland.Contracts.PluginNotification notification) => true;
        public void Dispose() { }
    }
}
