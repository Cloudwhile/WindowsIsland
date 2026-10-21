using WindowsIsland.Services;

if (args.FirstOrDefault() == "--read-system-dnd" && OperatingSystem.IsWindows())
{
    Console.WriteLine(new DoNotDisturbPreferences(new WindowsQuietHoursStore()).Read());
    return;
}

if (args.FirstOrDefault() == "--restart-parent-fixture")
{
    await File.WriteAllTextAsync(args[1], "ready");
    await Task.Delay(TimeSpan.FromSeconds(60));
    return;
}

if (args.FirstOrDefault() == "--update-fixture")
{
    var noConsole = args.Contains("--no-console", StringComparer.Ordinal);
    if (noConsole) UpdateFixtureConsole.Detach();
    var directory = args[1];
    var installer = Path.Combine(directory, "WindowsIsland-" + ReleaseVersion.Current.Display + "-win-x64.exe");
    using var update = new PreparedUpdate(directory, Path.Combine(directory, "payload"), File.Exists(installer) ? installer : null,
        File.Exists(Path.Combine(directory, "expected-version.txt")) ? File.ReadAllText(Path.Combine(directory, "expected-version.txt"))
            : ReleaseVersion.Current.Display, Path.GetDirectoryName(directory)!);
    try { await UpdateInstaller.StartAsync(update, CancellationToken.None, @"Local\WindowsIsland.UpdateTests." + Path.GetFileName(directory),
        restoreRegistration: args.Contains("--restore-registration", StringComparer.Ordinal)); }
    catch (Exception error)
    {
        if (!noConsole) Console.Error.WriteLine(error.Message);
        Environment.ExitCode = 1;
    }
    return;
}
if (args.FirstOrDefault() == "/i" && File.Exists(Path.Combine(AppContext.BaseDirectory, "msi-fixture-exit.txt")))
{
    var code = int.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "msi-fixture-exit.txt")));
    var marker = Path.Combine(AppContext.BaseDirectory, "msi.args");
    await File.WriteAllTextAsync(marker + ".tmp", string.Join(' ', args));
    File.Move(marker + ".tmp", marker, overwrite: true);
    if (code is 0 or 3010) await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "a-library.dll"), "new MSI library");
    Environment.ExitCode = code;
    return;
}
if (args.Contains("--settings"))
{
    var restartArguments = Path.Combine(AppContext.BaseDirectory, "restart.args");
    await File.WriteAllTextAsync(restartArguments + ".tmp", string.Join(' ', args));
    File.Move(restartArguments + ".tmp", restartArguments, overwrite: true);
    return;
}

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    checks++;
    Console.WriteLine($"PASS {name}");
}

var created = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
var old = new IslandNotification(1, created, "Mail", "Old", "Already in Action Center");
var next = new IslandNotification(2, created.AddSeconds(1), "Mail", "New", "New arrival");
var newest = new IslandNotification(3, created.AddSeconds(2), "Chat", "Newest", "New arrival");
var tracker = new NotificationTracker();
Check(tracker.Update([old]).Count == 0, "Startup suppresses history");
Check(tracker.Update([old, next]).Single() == next, "New notification emitted once");
Check(tracker.Update([old, next]).Count == 0, "Polling does not repeat notifications");
Check(tracker.Update([next]).Count == 0, "Removal does not create a popup");
Check(tracker.Update([next, old with { CreatedAt = created.AddMinutes(1) }]).Count == 1,
    "Reused ID with new creation time is new");
tracker.Reset();
Check(tracker.Update([old, next]).Count == 0, "Permission recovery re-baselines history");
Check(tracker.Update([newest, next, old]).Single() == newest, "Input order is irrelevant");
tracker.Reset();
tracker.Update([]);
Check(tracker.Update([newest, next]).SequenceEqual(new[] { next, newest }), "Bursts sorted chronologically");

var time = new ManualTime();
var presentation = new IslandScheduler(time);
Check(presentation.Message is null && !presentation.MessageIsExpired, "Startup has no active notification");
presentation.MessageArrived(next);
time.Advance(4.9);
Check(!presentation.MessageIsExpired, "Popup stays visible before deadline");
time.Advance(0.1);
Check(presentation.MessageIsExpired, "Popup closes after five seconds");
presentation.MessageArrived(next);
Check(presentation.Message is not null && !presentation.MessageIsExpired, "New notification resets deadline");
time.Advance(5);
Check(presentation.MessageIsExpired, "Replacement notification gets a fresh five seconds");
presentation.MessageDismissed();
Check(presentation.Message is null && !presentation.MessageIsExpired, "Clearing a notification cancels deadline");
presentation.MessageArrived(next);
time.Advance(5);
Check(presentation.MessageIsExpired, "Each new notification returns to idle after its deadline");
presentation.MessageDismissed();
time.Advance(100);
Check(!presentation.MessageIsExpired, "Cleared notification stays idle");

var instanceName = $"WindowsIsland.Tests.{Guid.NewGuid():N}";
using (var primary = SingleInstance.TryAcquire(instanceName))
{
    Check(primary is not null, "First launch owns the tray listener");
    using var duplicate = SingleInstance.TryAcquire(instanceName);
    Check(duplicate is null, "Repeated launch does not create another tray listener");
}
using (var restarted = SingleInstance.TryAcquire(instanceName))
{
    Check(restarted is not null, "Tray listener can restart after exit");
    restarted!.Dispose();
    restarted.Dispose();
    using var afterDispose = SingleInstance.TryAcquire(instanceName);
    Check(afterDispose is not null, "Instance cleanup is idempotent and releases the listener");
}
using (var observer = new Mutex(false, instanceName))
{
    using var reopened = SingleInstance.TryAcquire(instanceName);
    Check(reopened is not null, "An observer handle cannot prevent acquisition of an unowned instance mutex");
    using var duplicate = SingleInstance.TryAcquire(instanceName);
    Check(duplicate is null, "An existing mutex cannot be recursively acquired as a second instance");
}
using (var abandoned = new Mutex(false, instanceName))
{
    var owner = new Thread(() => abandoned.WaitOne());
    owner.Start();
    owner.Join();
    using var recovered = SingleInstance.TryAcquire(instanceName);
    Check(recovered is not null, "An abandoned instance mutex allows recovery after a crashed owner");
}
var mergeTime = new ManualTime();
var router = new MessageRouter(mergeTime);
var hook = new IslandNotification(10, created, "微信", "小明", "稍后见。", Source: NotificationSource.ClientHook,
    AppId: "wechat", EventId: "wechat/1");
var system = hook with { Id = 11, Source = NotificationSource.SystemNotification, AppName = "WeChat", AppId = "Tencent.WeChat", EventId = null };
Check(router.Receive(hook) is null && router.HasPending, "Hook waits for a matching system notification");
mergeTime.Advance(1);
Check(router.Flush().Count == 0, "Hook does not race the system polling interval");
Check(router.Receive(system)?.Notification == system && !router.HasPending, "System notification replaces a pending hook before display");
mergeTime.Advance(2);
Check(router.Flush().Count == 0, "Merged hook never produces a second popup");
Check(router.Receive(hook with { EventId = "wechat/2" }) is null && !router.HasPending, "System-first delivery suppresses a matching hook");

router.Clear();
router.Receive(hook);
mergeTime.Advance(1.5);
Check(router.Flush().Single().Notification == hook, "Hook alone is displayed after the merge delay");
Check(router.Receive(system)?.ReplaceCurrent == true, "Late system content updates the displayed hook without another popup");
Check(router.Receive(system with { Id = 12, CreatedAt = created.AddSeconds(2) })?.ReplaceCurrent == false,
    "Another real system notification with the same text is still displayed");
Check(!MessageRouter.Matches(hook, system with { AppName = "Telegram", AppId = "telegram" }), "Identical messages from different apps do not merge");
Check(!MessageRouter.Matches(hook, system with { Title = "小红" }), "Identical bodies from different senders do not merge");
Check(!MessageRouter.Matches(hook, system with { CreatedAt = created.AddSeconds(20) }), "Old messages cannot suppress a later message");
Check(MessageRouter.Matches(hook, system with { Body = "稍后\n见。" }), "Formatting differences do not create a duplicate");
Check(MessageRouter.Matches(hook with { Body = "这是一段较长的通知正文内容" }, system with { Body = "这是一段较长的通知…" }),
    "Truncated previews can merge with full system content");
var malformedText = "通知" + (char)0xD800 + "正文";
Check(MessageRouter.Matches(hook with { Body = malformedText }, system with { Body = malformedText }),
    "Malformed UTF-16 notification text cannot crash duplicate matching");
Check(!MessageRouter.Matches(hook with { Body = malformedText }, system with { Body = "另一条通知" }),
    "Repairing malformed scalars does not merge unrelated messages");

router.Clear();
router.Receive(hook);
router.Receive(hook);
mergeTime.Advance(1.5);
Check(router.Flush().Count == 1, "Repeated hook events for the same popup emit once");
router.Clear();
router.Receive(system);
router.ClearSystemHistory();
router.Receive(hook);
Check(router.HasPending, "Notification permission loss does not disable local client hooks");
router.Clear();
Check(!router.HasPending && router.Flush().Count == 0, "Exit clears pending messages");

byte[] localLogo = [1, 2, 3], avatarPhoto = [4, 5, 6], systemLogo = [7, 8, 9];
var illustratedHook = hook with { AppIcon = localLogo, SenderAvatar = avatarPhoto };
router.Receive(illustratedHook);
var mergedImages = router.Receive(system)!;
Check(mergedImages.Notification.AppIcon == localLogo && mergedImages.Notification.SenderAvatar == avatarPhoto
    && !mergedImages.ReplaceCurrent && !router.HasPending, "System delivery preserves a pending hook's application icon and conversation avatar");
router.Clear();
router.Receive(illustratedHook);
var preferredLogo = router.Receive(system with { AppIcon = systemLogo })!.Notification;
Check(preferredLogo.AppIcon == systemLogo && preferredLogo.SenderAvatar == avatarPhoto,
    "The system application icon takes priority while the hook supplies the conversation avatar");
router.Clear();
router.Receive(system);
var imageUpdate = router.Receive(illustratedHook)!;
Check(imageUpdate.ReplaceCurrent && imageUpdate.Notification.AppIcon == localLogo && imageUpdate.Notification.SenderAvatar == avatarPhoto
    && imageUpdate.Notification.Id == system.Id && imageUpdate.Notification.Source == NotificationSource.SystemNotification,
    "A hook arriving after system delivery updates the existing popup with both images");
Check(router.Receive(illustratedHook) is null && router.Flush().Count == 0 && !router.HasPending,
    "Repeated image delivery does not redisplay a system notification");
router.Clear();
router.Receive(illustratedHook);
mergeTime.Advance(1.5);
router.Flush();
var replacementImages = router.Receive(system)!;
Check(replacementImages.ReplaceCurrent && replacementImages.Notification.AppIcon == localLogo && replacementImages.Notification.SenderAvatar == avatarPhoto,
    "A late system replacement retains both images already shown by the local popup");
router.Clear();
router.Receive(system);
Check(router.Receive(illustratedHook with { Title = "另一位联系人" }) is null && router.HasPending,
    "Images from a different conversation cannot replace the active system notification");
router.Clear();
router.Receive(system);
Check(router.Receive(hook with { AppIcon = [], SenderAvatar = [] }) is null,
    "Empty image payloads do not cause a redundant popup update");
router.Clear();

var battery = new PowerTracker();
var unplugged = new PowerSnapshot(true, false, false, 50);
Check(battery.Update(unplugged) is null, "Startup does not replay the current power state");
var plugged = unplugged with { Connected = true, Charging = true };
Check(battery.Update(plugged)?.Title == "开始充电", "Connecting a charger displays one charging event");
Check(battery.Update(plugged) is null, "Duplicate power broadcasts do not repeat the charging popup");
Check(battery.Update(plugged with { Percent = 51 }) is null, "Normal battery percentage changes stay silent");
Check(battery.Update(plugged with { Percent = 100, Charging = false })?.Title == "电池已充满", "Charge completion produces one event");
Check(battery.Update(plugged with { Percent = 100, Charging = false }) is null, "Full battery status does not continuously light the island");
Check(battery.Update(unplugged)?.Title == "已断开电源", "Unplugging the charger displays an event");
Check(battery.Update(unplugged with { Connected = true })?.Title == "已连接电源", "External power without active charging is described accurately");
Check(battery.Update(plugged)?.Title == "开始充电", "Charging can start after power has already been connected");
var desktop = new PowerTracker();
Check(desktop.Update(new(false, true, false, null)) is null && desktop.Update(new(false, false, false, null)) is null,
    "Computers without batteries do not show charging events");
var powerEvent = new PowerTracker();
powerEvent.Update(unplugged);
var powerNotification = powerEvent.Update(plugged)!;
Check(powerNotification.Source == NotificationSource.Power && router.Receive(powerNotification)?.Notification == powerNotification,
    "Power events do not depend on system notification permission");

Check(MessengerIdentity.FromProcess("Weixin") == "wechat" && MessengerIdentity.FromProcess("Telegram") == "telegram", "Local hook clients are recognized");
Check(MessengerIdentity.FromProcess("QQ") is null && MessengerIdentity.FromProcess("QQNT") is null,
    "QQ uses Windows notifications without any client hook");
Check(MessengerIdentity.FromProcess("QQBrowser") is null, "Unrelated clients are not monitored");
Check(MessengerPopupRules.IsMessagePopup("QtNotification", 0x80000000, 0x88, 320, 160, false), "A local message popup is eligible for the hook");
Check(!MessengerPopupRules.IsMessagePopup("QtNotification", 0x80070000, 0x88, 320, 160, false), "Resizable chat windows are excluded");
Check(!MessengerPopupRules.IsMessagePopup("tooltips_class32", 0x80000000, 0x88, 320, 160, false), "Tooltips are not treated as messages");
Check(!MessengerPopupRules.IsMessagePopup("#32768", 0x80000000, 0x88, 320, 160, false), "Client menus are not treated as messages");
Check(!MessengerPopupRules.IsMessagePopup("QtNotification", 0x80000000, 0x88, 1100, 750, false), "Main client windows are excluded");
LocalizationChecks.Run(Check);
MarkdownChecks.Run(Check);
await IslandSchedulerChecks.RunAsync(Check);
await PinnedPanelChecks.RunAsync(Check);
GameModeChecks.Run(Check);
DoNotDisturbChecks.Run(Check);
WindowsPreferencesChecks.Run(Check);
SettingsChecks.Run(Check);
WeChatChecks.Run(Check);
WeChatRoutingChecks.Run(Check);
StatusChecks.Run(Check);
ToastImageChecks.Run(Check);
NotificationImageStoreChecks.Run(Check);
await DiagnosticChecks.RunAsync(Check);
await DiagnosticReportCacheChecks.RunAsync(Check);
await WindowsIsland.LogicTests.PluginDirectoryChecks.RunAsync();
await WindowsIsland.LogicTests.PluginEvolutionChecks.RunAsync();
await WindowsIsland.LogicTests.PluginInstallationChecks.RunAsync();
await WindowsIsland.LogicTests.PluginSourceChecks.RunAsync();
await PluginChecks.RunAsync(Check);
await WindowsIsland.LogicTests.PluginManagerChecks.RunAsync(Check);
await RegisteredRestartChecks.RunAsync(Check);
await InitializationRegistrationChecks.RunAsync(Check);
await UpdateChecks.RunAsync(Check);
await UpdateCheckServiceChecks.RunAsync(Check);
Console.WriteLine($"{checks} checks passed.");

sealed class ManualTime : TimeProvider
{
    private long _timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _timestamp;
    public void Advance(double seconds) => _timestamp += TimeSpan.FromSeconds(seconds).Ticks;
}
