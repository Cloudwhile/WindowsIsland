using System.Text.Json;
using WindowsIsland.Services;

internal static class DiagnosticReportCacheChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var clock = new ManualTime();
        var calls = 0;
        using var cache = new DiagnosticReportCache(() => Interlocked.Increment(ref calls).ToString(), clock);
        var first = await cache.GetAsync();
        clock.Advance(59);
        check(await cache.GetAsync() == first && calls == 1, "Diagnostic requests within sixty seconds reuse the first report");
        clock.Advance(1);
        check(await cache.GetAsync() != first && calls == 2, "Diagnostic reports refresh after the reuse window expires");

        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrentCalls = 0;
        using var concurrent = new DiagnosticReportCache(() =>
        {
            var number = Interlocked.Increment(ref concurrentCalls);
            if (number == 1)
            {
                entered.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            }
            return number.ToString();
        });
        try
        {
            var pending = concurrent.GetAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(Enumerable.Range(0, 20).All(_ => ReferenceEquals(pending, concurrent.GetAsync())) && concurrentCalls == 1,
                "Concurrent diagnostic actions share the in-flight generation");
            concurrent.Invalidate();
            try { await pending; check(false, "Cleared pending report is canceled"); }
            catch (OperationCanceledException) { check(true, "Clearing diagnostics rejects an in-flight stale report"); }
            check(await concurrent.GetAsync() == "2", "Clearing diagnostics permits a fresh report immediately");
        }
        finally { release.Set(); }

        var failures = 0;
        using var retry = new DiagnosticReportCache(() => ++failures == 1 ? throw new IOException() : "ready");
        try { await retry.GetAsync(); check(false, "Generation error is surfaced"); }
        catch (IOException) { check(true, "Diagnostic generation failures remain visible to the caller"); }
        check(await retry.GetAsync() == "ready" && failures == 2, "Failed reports do not block a manual retry");
        retry.Dispose();
        try { await retry.GetAsync(); check(false, "Disposed report cache rejects requests"); }
        catch (ObjectDisposedException) { check(true, "Closed diagnostics cannot generate another report"); }

        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "diagnostic-cache", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var store = new DiagnosticStore(directory, true);
        store.Record(DiagnosticArea.WeChat, "ReadLoop", new InvalidOperationException());
        var captured = await store.Reports.GetAsync();
        store.Record(DiagnosticArea.WeChat, "AccessibilityRestored");
        store.SetEnabled(false);
        var path = Path.Combine(directory, "report.json");
        await store.ExportAsync(path);
        check(await File.ReadAllTextAsync(path) == captured && await store.Reports.GetAsync() == captured,
            "Manual, preview, copy and export share identical report content and timestamps");
        check(store.Snapshot().Length == 2 && !store.Enabled,
            "Generating and reusing reports neither appends events nor enables collection");
        store.Clear();
        var cleared = JsonSerializer.Deserialize<DiagnosticReport>(await store.Reports.GetAsync(), DiagnosticStore.Json)!;
        check(cleared.Events.Length == 0 && await File.ReadAllTextAsync(path) == captured,
            "Clearing records invalidates report reuse without deleting an exported report");
    }
}
