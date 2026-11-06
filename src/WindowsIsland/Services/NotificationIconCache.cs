namespace WindowsIsland.Services;

internal sealed class NotificationIconCache(int capacity = 64)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (byte[] Bytes, LinkedListNode<string> Node)> _cached = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<byte[]?>> _loading = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recent = new();

    public Task<byte[]?> GetAsync(string key, Func<Task<byte[]?>> load)
    {
        TaskCompletionSource<byte[]?> completion;
        lock (_gate)
        {
            if (_cached.TryGetValue(key, out var cached))
            {
                _recent.Remove(cached.Node);
                _recent.AddLast(cached.Node);
                return Task.FromResult<byte[]?>(cached.Bytes);
            }
            if (_loading.TryGetValue(key, out completion!)) return completion.Task;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _loading.Add(key, completion);
        }
        _ = LoadAsync(key, load, completion);
        return completion.Task;
    }

    private async Task LoadAsync(string key, Func<Task<byte[]?>> load, TaskCompletionSource<byte[]?> completion)
    {
        byte[]? bytes = null;
        try { bytes = await load().ConfigureAwait(false); }
        catch (Exception) { }
        if (bytes is not { Length: > 0 and <= 4194304 }) bytes = null;
        lock (_gate)
        {
            // Clear invalidates old loads without letting their completion overwrite a new load.
            if (_loading.TryGetValue(key, out var current) && ReferenceEquals(current, completion))
            {
                _loading.Remove(key);
                if (bytes is not null)
                {
                    if (_cached.Count >= capacity && _recent.First is { } oldest)
                    {
                        _cached.Remove(oldest.Value);
                        _recent.RemoveFirst();
                    }
                    _cached.Add(key, (bytes, _recent.AddLast(key)));
                }
            }
        }
        completion.TrySetResult(bytes);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _cached.Clear();
            _recent.Clear();
            _loading.Clear();
        }
    }
}
