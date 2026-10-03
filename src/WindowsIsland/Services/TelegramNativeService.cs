using Microsoft.UI.Dispatching;

namespace WindowsIsland.Services;

internal sealed class TelegramNativeService(DispatcherQueue dispatcher, bool fixture = false) : IDisposable
{
    private readonly Dictionary<int, TelegramHookSession> _sessions = [];
    private readonly HashSet<int> _starting = [], _unsupported = [];
    private bool _disposed;
    public event Action<IslandNotification>? Received;
    public bool IsConnected(int processId) => _sessions.GetValueOrDefault(processId)?.IsConnected == true;

    public void RefreshClients(IReadOnlyCollection<int> clients)
    {
        if (_disposed) return;
        foreach (var id in _sessions.Keys.Where(id => !clients.Contains(id)).ToArray())
        {
            _sessions[id].Dispose();
            _sessions.Remove(id);
        }
        _unsupported.RemoveWhere(id => !clients.Contains(id));
        foreach (var id in clients)
        {
            if (_starting.Contains(id) || _unsupported.Contains(id) || _sessions.GetValueOrDefault(id)?.IsConnected == true) continue;
            if (_sessions.Remove(id, out var old)) old.Dispose();
            _starting.Add(id);
            var session = new TelegramHookSession(id, notification => dispatcher.TryEnqueue(() =>
            {
                if (!_disposed) Received?.Invoke(notification with { OriginProcessId = id });
            }));
            _sessions[id] = session;
            _ = AttachAsync(id, session);
        }
    }

    private async Task AttachAsync(int id, TelegramHookSession session)
    {
        var unsupported = false;
        try { await session.AttachAsync(Path.Combine(AppContext.BaseDirectory, "WindowsIsland.TelegramHook.dll"), fixture); }
        catch (NotSupportedException) { unsupported = true; session.Dispose(); }
        catch (Exception) { session.Dispose(); }
        finally
        {
            dispatcher.TryEnqueue(() =>
            {
                if (_disposed) return;
                if (unsupported) _unsupported.Add(id);
                _starting.Remove(id);
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();
        Received = null;
    }
}
