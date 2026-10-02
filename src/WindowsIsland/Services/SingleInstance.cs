namespace WindowsIsland.Services;

internal sealed class SingleInstance : IDisposable
{
    private Mutex? _mutex;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    public static SingleInstance? TryAcquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        if (createdNew) return new SingleInstance(mutex);
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (_mutex is null) return;
        _mutex.ReleaseMutex();
        _mutex.Dispose();
        _mutex = null;
    }
}
