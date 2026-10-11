namespace WindowsIsland.Services;

internal sealed class SingleInstance : IDisposable
{
    private static readonly HashSet<string> Owned = new(StringComparer.Ordinal);
    private Mutex? _mutex;
    private readonly string _name;

    private SingleInstance(Mutex mutex, string name) { _mutex = mutex; _name = name; }

    public static SingleInstance? TryAcquire(string name)
    {
        lock (Owned)
        {
            // A mutex can outlive its owner while another process still holds a handle.
            // Prevent recursive acquisition in this process, then test ownership, not existence.
            if (Owned.Contains(name)) return null;
            var mutex = new Mutex(initiallyOwned: false, name);
            try
            {
                bool acquired;
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (acquired) { Owned.Add(name); return new SingleInstance(mutex, name); }
            }
            catch { mutex.Dispose(); throw; }
            mutex.Dispose();
            return null;
        }
    }

    public void Dispose()
    {
        lock (Owned)
        {
            if (_mutex is null) return;
            _mutex.ReleaseMutex();
            _mutex.Dispose();
            _mutex = null;
            Owned.Remove(_name);
        }
    }
}
