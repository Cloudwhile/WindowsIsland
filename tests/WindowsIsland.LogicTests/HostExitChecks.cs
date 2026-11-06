using System.Collections.Concurrent;
using WindowsIsland;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

internal static class HostExitChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        using var dispatcher = new TestDispatcher();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Post(async _ =>
        {
            try { await CheckExitAsync(check); finished.SetResult(); }
            catch (Exception error) { finished.SetException(error); }
        }, null);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task CheckExitAsync(Action<bool, string> check)
    {
        var store = new SettingsStore(Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "host-exit-" + Guid.NewGuid().ToString("N"), "settings.json"));
        var source = new DelayedSource();
        using var manager = new PluginManager([new(BuiltInPluginDefinitions.Power, () => source)], store);
        await manager.StartAsync();
        var window = new MainWindow(manager);
        var exit = window.ExitApplicationAsync();
        try
        {
            await source.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(!exit.IsCompleted && window.Closes == 0 && !source.Disposed,
                "Host retains its final window while actual plugin cleanup is pending");
            check(ReferenceEquals(exit, window.ExitApplicationAsync()) && window.Preparations == 1,
                "Repeated exit and restart callers share one shutdown task");
            // A queued UI callback must run while shutdown is waiting; it also releases cleanup.
            SynchronizationContext.Current!.Post(_ => source.Release.TrySetResult(), null);
            await exit.WaitAsync(TimeSpan.FromSeconds(5));
            check(source.Disposed && source.Stops == 1 && window.Closes == 1,
                "Final window closes only after plugin stop and disposal complete");
            check(window.CloseThread == window.OwnerThread && source.StopThread == window.OwnerThread,
                "Plugin lifecycle and final Close retain the dispatcher context without blocking it");
            check(ReferenceEquals(exit, window.ExitApplicationAsync()) && window.Closes == 1,
                "Completed shutdown cannot close the final window twice");
        }
        finally { source.Release.TrySetResult(); await manager.ShutdownAsync(); }

        using var empty = new PluginManager([], store);
        var failedClose = new MainWindow(empty) { CloseFailure = new InvalidOperationException("controlled close failure") };
        var failure = failedClose.ExitApplicationAsync();
        try { await failure; check(false, "Close failure must reach an awaiting restart caller"); }
        catch (InvalidOperationException error) when (ReferenceEquals(error, failedClose.CloseFailure))
        {
            check(failedClose.Closes == 1 && failedClose.Preparations == 1,
                "Exit failures reach restart callers without swallowing cleanup errors");
        }

        failedClose.CloseFailure = null;
        await failedClose.ExitApplicationAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(failedClose.Closes == 2 && failedClose.Preparations == 1,
            "A failed exit clears the shared task so a later attempt can retry Close after cleanup already finished");

        using var managerForFailure = new PluginManager([], store);
        var attempts = 0;
        var blocked = new MainWindow(managerForFailure)
        {
            ShutdownOverride = () =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    return Task.FromException(new InvalidOperationException("controlled cleanup failure"));
                return Task.CompletedTask;
            }
        };
        try { await blocked.ExitApplicationAsync(); check(false, "Cleanup failure must remain visible"); }
        catch (InvalidOperationException)
        {
            check(blocked.Closes == 0 && blocked.Preparations == 1 && attempts == 1,
                "Cleanup failure leaves the final window open and does not claim a successful exit");
        }
        await blocked.ExitApplicationAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(blocked.Closes == 1 && blocked.Preparations == 1 && attempts == 2,
            "A later exit retries remaining cleanup without repeating PrepareForExit");
    }

    private sealed class DelayedSource : IMessageSource
    {
        public string Id => BuiltInPluginIds.Power;
        public string DisplayName => "Delayed shutdown fixture";
        public event Action<PluginNotification>? NotificationReceived { add { } remove { } }
        public readonly TaskCompletionSource Stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Stops, StopThread;
        public bool Disposed;
        public Task InitializeAsync(IPluginContext context) => Task.CompletedTask;
        public Task StartAsync() => Task.CompletedTask;
        public async Task StopAsync()
        {
            Stops++;
            Stopping.TrySetResult();
            await Release.Task;
            StopThread = Environment.CurrentManagedThreadId;
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class TestDispatcher : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;
        public TestDispatcher()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var work in _queue.GetConsumingEnumerable()) work.Callback(work.State);
            }) { IsBackground = true };
            _thread.Start();
        }
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Add((callback, state));
        public void Dispose() => _queue.CompleteAdding();
    }
}

namespace WindowsIsland
{
    // Compile the production exit partial against a window shell, with the real PluginManager.
    public sealed partial class MainWindow
    {
        private readonly PluginManager _plugins;
        private bool _closed, _exiting;
        internal int Preparations, Closes, CloseThread;
        internal readonly int OwnerThread = Environment.CurrentManagedThreadId;
        internal Exception? CloseFailure;
        internal Func<Task>? ShutdownOverride;
        internal MainWindow(PluginManager plugins) => _plugins = plugins;
        private void PrepareForExit() => Preparations++;
        private Task ShutdownPluginsCoreAsync() => ShutdownOverride?.Invoke() ?? _plugins.ShutdownAsync();
        private void Close()
        {
            if (!_exiting) throw new InvalidOperationException("Close attempted before cleanup completed.");
            Closes++;
            CloseThread = Environment.CurrentManagedThreadId;
            if (CloseFailure is { } error) throw error;
            _closed = true;
        }
    }
}
