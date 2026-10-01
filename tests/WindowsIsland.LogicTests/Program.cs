using WindowsIsland.Services;

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
var presentation = new NotificationPresentation(time);
presentation.Show();
time.Advance(4.9);
Check(!presentation.Expired, "Popup stays visible before deadline");
time.Advance(0.1);
Check(presentation.Expired, "Popup closes after five seconds");
presentation.Show();
Check(presentation.Active && !presentation.Expired, "New notification resets deadline");
time.Advance(5);
Check(presentation.Expired, "Replacement notification gets a fresh five seconds");
presentation.Clear();
Check(!presentation.Active && !presentation.Expired, "Clearing a notification cancels deadline");
presentation.Show();
time.Advance(5);
Check(presentation.Expired, "Each new notification returns to idle after its deadline");
presentation.Clear();
time.Advance(100);
Check(!presentation.Expired, "Cleared notification stays idle");
Console.WriteLine($"{checks} checks passed.");

sealed class ManualTime : TimeProvider
{
    private long _timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _timestamp;
    public void Advance(double seconds) => _timestamp += TimeSpan.FromSeconds(seconds).Ticks;
}
