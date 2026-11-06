using WindowsIsland.Services;

internal static class NotificationIconCacheChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var cache = new NotificationIconCache();
        var pending = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        var readers = Enumerable.Range(0, 100).Select(_ => Task.Run(() => cache.GetAsync("one", () =>
        {
            Interlocked.Increment(ref loads);
            return pending.Task;
        }))).ToArray();
        var bytes = new byte[] { 1 };
        pending.SetResult(bytes);
        check((await Task.WhenAll(readers)).All(value => ReferenceEquals(value, bytes)) && loads == 1,
            "One hundred concurrent icon reads share one load");
        for (var i = 0; i < 63; i++) await cache.GetAsync(i.ToString(), () => Task.FromResult<byte[]?>([2]));
        await cache.GetAsync("one", () => throw new Exception("Cached icon was lost"));
        await cache.GetAsync("new", () => Task.FromResult<byte[]?>([3]));
        var misses = 0;
        for (var i = 1; i < 63; i++)
            await cache.GetAsync(i.ToString(), () => { misses++; return Task.FromResult<byte[]?>([4]); });
        await cache.GetAsync("one", () => { misses++; return Task.FromResult<byte[]?>([4]); });
        await cache.GetAsync("new", () => { misses++; return Task.FromResult<byte[]?>([4]); });
        check(misses == 0, "A full icon cache preserves all entries except the least recently used icon");
        await cache.GetAsync("0", () => { misses++; return Task.FromResult<byte[]?>([5]); });
        check(misses == 1, "The least recently used icon is evicted individually");
        var old = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRead = cache.GetAsync("stale", () => old.Task);
        cache.Clear();
        await cache.GetAsync("stale", () => Task.FromResult<byte[]?>(bytes));
        old.SetResult([9]);
        await oldRead;
        check(ReferenceEquals(await cache.GetAsync("stale", () => throw new Exception()), bytes),
            "An in-flight icon read cannot repopulate a cleared cache with stale bytes");
    }
}
