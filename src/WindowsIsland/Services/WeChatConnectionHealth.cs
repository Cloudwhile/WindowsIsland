namespace WindowsIsland.Services;

// Settings show only outcome states. Consecutive accessibility failures degrade a
// running client from "not connected" to an explicit failure instead of an
// optimistic listening state; any readable client is reported as connected.
internal sealed class WeChatConnectionHealth
{
    internal const int FailureThreshold = 3;
    private readonly Dictionary<int, (int Count, bool Unsupported)> _failures = [];

    public void Succeeded(int client) => _failures.Remove(client);

    public void Failed(int client, bool unsupported)
    {
        _failures.TryGetValue(client, out var previous);
        _failures[client] = (Math.Min(previous.Count + 1, FailureThreshold), unsupported);
    }

    public void Remove(int client) => _failures.Remove(client);
    public void Clear() => _failures.Clear();

    // A retry gets one more attempt: the next failure degrades again immediately.
    public void Retry()
    {
        foreach (var client in _failures.Keys.ToArray())
            _failures[client] = (Math.Min(_failures[client].Count, FailureThreshold - 1), _failures[client].Unsupported);
    }

    public string StatusKey(bool connected, int clients)
    {
        if (connected) return "Connected";
        if (clients == 0) return "NotRunning";
        var failed = _failures.Values.Where(item => item.Count >= FailureThreshold).ToArray();
        if (failed.Length == 0) return "NotConnected";
        return failed.Any(item => item.Unsupported) ? "WeChatUnsupported" : "WeChatConnectFailed";
    }

    public static bool IsUnsupported(string failure) => failure is "ModuleNotFound" or "UnsupportedImage" or "ImageSizeMismatch"
        or "MemoryLayout" or "HeaderMismatch" or "AccessorMismatch" or "ConsumerMismatch" or "InvalidFlag";
}
