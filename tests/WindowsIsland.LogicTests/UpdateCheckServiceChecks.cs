using System.Net.Http;
using WindowsIsland.Services;

internal static class UpdateCheckServiceChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WindowsIsland-update-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            var entered = Signal();
            var response = new TaskCompletionSource<GitHubRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = 0;
            var channels = new List<bool>();
            using var service = new UpdateCheckService(store, (include, _) =>
            {
                Interlocked.Increment(ref requests);
                channels.Add(include);
                entered.TrySetResult();
                return response.Task;
            });
            var startup = service.StartOnce();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(service.State.Busy && !startup.IsCompleted, "Update query leaves the caller responsive");
            check(ReferenceEquals(startup, service.CheckAsync()) && ReferenceEquals(startup, service.StartOnce()),
                "Overlapping automatic and manual requests share one operation");
            var release = Candidate();
            response.SetResult(release);
            await startup;
            check(service.State is { Checked: true, Busy: false } && service.State.Candidate == release && requests == 1,
                "Completed update results remain available without a settings window");
            await service.StartOnce();
            check(requests == 1 && channels.SequenceEqual([false]), "Startup checks only once using the stored stable channel");
            var cached = service.State;
            store.Save(store.Current with { Language = "en-US", Animations = false });
            check(ReferenceEquals(cached, service.State), "Unrelated preferences retain update results");

            entered = Signal();
            response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldResponse = response;
            var old = service.CheckAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            store.Save(store.Current with { IncludePrereleaseUpdates = true });
            check(service.State == new UpdateCheckState(), "Channel changes invalidate the candidate immediately");
            await old.WaitAsync(TimeSpan.FromSeconds(5));
            entered = Signal();
            response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var next = service.CheckAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            oldResponse.SetResult(release);
            response.SetResult(null);
            await next;
            check(service.State is { Checked: true, Candidate: null } && channels.Last(),
                "Late old-channel responses cannot replace the current channel result");

            entered = Signal();
            response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancel = service.CheckAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            service.Cancel();
            await cancel.WaitAsync(TimeSpan.FromSeconds(5));
            check(service.State is { Canceled: true, Busy: false, Checked: false }, "Cancel does not report an up-to-date installation");
            response.SetResult(release);

            var failures = 0;
            using var retry = new UpdateCheckService(store, (_, _) =>
            {
                if (++failures == 1) throw new HttpRequestException();
                return Task.FromResult<GitHubRelease?>(release);
            });
            await retry.StartOnce();
            check(retry.State is { ErrorKey: "UpdateFetchError", Checked: false, Busy: false },
                "Offline startup is contained and remains distinct from no updates");
            await retry.StartOnce();
            check(failures == 1, "Failed startup does not cause an automatic retry loop");
            await retry.CheckAsync();
            check(retry.State.Candidate == release && failures == 2, "Manual retry accepts synchronous completion after failure");

            var never = new TaskCompletionSource<GitHubRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var timeout = new UpdateCheckService(store, (_, _) => never.Task, TimeSpan.FromMilliseconds(30));
            await timeout.StartOnce().WaitAsync(TimeSpan.FromSeconds(5));
            check(timeout.State is { Canceled: true, Busy: false }, "Timeout releases the operation even if transport ignores cancellation");
            never.SetResult(release);

            entered = Signal();
            var closedResponse = new TaskCompletionSource<GitHubRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var closed = new UpdateCheckService(store, (_, _) => { entered.TrySetResult(); return closedResponse.Task; });
            var closing = closed.StartOnce();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            closed.Dispose();
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            closedResponse.SetResult(release);
            await closed.CheckAsync();
            check(closed.State == new UpdateCheckState(), "Application shutdown rejects late results and new queries");

            var manualRequests = 0;
            using var manual = new UpdateCheckService(store, (_, _) =>
            {
                Interlocked.Increment(ref manualRequests);
                return Task.FromResult<GitHubRelease?>(null);
            });
            await manual.CheckAsync();
            await manual.StartOnce();
            check(manualRequests == 1, "A manual request before startup prevents a redundant startup query");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static GitHubRelease Candidate()
    {
        var version = new ReleaseVersion(9, 0, 0, "", "9.0.0");
        var uri = new Uri("https://github.com/Cloudwhile/WindowsIsland/releases/tag/v9.0.0");
        return new(version, version.Tag, "Update", "", uri, false,
            new("package.zip", uri, 100), new("package.zip.sha256", uri, 64), null, null);
    }
}
