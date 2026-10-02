using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;

namespace WindowsIsland.Services;

internal sealed class MessengerHookService : IDisposable
{
    private sealed record Client(int ProcessId, string Identity, string Executable, nint Hook);
    private sealed record Request(nint Window, Client Client, DateTimeOffset CreatedAt, bool Reset = false);
    private readonly DispatcherQueue _dispatcher;
    private readonly string? _clientDirectory;
    private readonly Func<string, bool> _isEnabled;
    private readonly Dictionary<int, Client> _clients = [];
    private readonly HashSet<nint> _baseline = [], _queued = [], _changed = [];
    private readonly BlockingCollection<Request> _requests = new(64);
    private readonly CancellationTokenSource _stop = new();
    private readonly WinEventProc _callback;
    private readonly GCHandle _callbackHandle;
    private readonly Thread _readerThread;
    private volatile bool _disposed;
    private bool _started;
    public event Action<IslandNotification>? Received;
    public Func<int, bool>? NativeTelegramConnected { get; set; }
    public string[] ConnectedApps => _clients.Values.Select(client => client.Identity).Distinct().ToArray();
    public int[] TelegramProcessIds => _clients.Values.Where(client => client.Identity == "telegram").Select(client => client.ProcessId).ToArray();

    public MessengerHookService(DispatcherQueue dispatcher, string? clientDirectory = null, Func<string, bool>? isEnabled = null)
    {
        _dispatcher = dispatcher;
        _isEnabled = isEnabled ?? (_ => true);
        _clientDirectory = clientDirectory is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(clientDirectory)) + Path.DirectorySeparatorChar;
        _callback = OnWinEvent;
        _callbackHandle = GCHandle.Alloc(_callback);
        _readerThread = new Thread(ReadLoop) { IsBackground = true, Name = "WindowsIsland.MessageHook" };
        _readerThread.SetApartmentState(ApartmentState.MTA);
    }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        RefreshClients();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            if (_clients.ContainsKey((int)pid) && IsWindowVisible(window)) _baseline.Add(window);
            return true;
        }, 0);
        _readerThread.Start();
    }

    public void RefreshClients()
    {
        if (_disposed) return;
        var alive = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var identity = MessengerIdentity.FromProcess(process.ProcessName);
                    if (identity is null || !_isEnabled(identity)) continue;
                    alive.Add(process.Id);
                    if (_clients.ContainsKey(process.Id)) continue;
                    var executable = process.MainModule?.FileName;
                    if (executable is null) continue;
                    if (_clientDirectory is not null && !executable.StartsWith(_clientDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                    var hook = SetWinEventHook(0x8000, 0x8010, 0, _callback, (uint)process.Id, 0, 2);
                    if (hook != 0) _clients[process.Id] = new(process.Id, identity, executable, hook);
                }
                catch (Exception) { }
            }
        }
        foreach (var id in _clients.Keys.Where(id => !alive.Contains(id)).ToArray())
        {
            UnhookWinEvent(_clients[id].Hook);
            _clients.Remove(id);
        }
    }

    private void OnWinEvent(nint hook, uint eventId, nint window, int objectId, int childId, uint thread, uint timestamp)
    {
        if (_disposed || window == 0 || eventId is not (0x8001 or 0x8002 or 0x8003 or 0x800C or 0x800E)) return;
        var root = GetAncestor(window, 2);
        if (root == 0) root = window;
        GetWindowThreadProcessId(root, out var pid);
        if (!_clients.TryGetValue((int)pid, out var client)) return;
        if (client.Identity == "telegram" && NativeTelegramConnected?.Invoke(client.ProcessId) == true) return;
        if (eventId is 0x8001 or 0x8003)
        {
            if (window != root || objectId != 0) return;
            _baseline.Remove(root);
            _changed.Remove(root);
            _requests.TryAdd(new(root, client, DateTimeOffset.UtcNow, Reset: true));
            return;
        }
        if (_baseline.Contains(root) || !IsPopup(root)) return;
        if (!_queued.Add(root)) { _changed.Add(root); return; }
        if (!_requests.TryAdd(new(root, client, DateTimeOffset.UtcNow))) _queued.Remove(root);
    }

    private static bool IsPopup(nint window)
    {
        if (!IsWindowVisible(window) || window == GetForegroundWindow() || !GetWindowRect(window, out var rect)) return false;
        var scale = Math.Max(1, GetDpiForWindow(window) / 96d);
        var className = new StringBuilder(256);
        GetClassName(window, className, className.Capacity);
        return MessengerPopupRules.IsMessagePopup(className.ToString(), GetWindowLongPtr(window, -16).ToInt64(),
            GetWindowLongPtr(window, -20).ToInt64(), (int)((rect.Right - rect.Left) / scale),
            (int)((rect.Bottom - rect.Top) / scale), GetWindow(window, 4) != 0);
    }

    private void ReadLoop()
    {
        var reader = new MessengerPopupReader();
        var last = new Dictionary<nint, (string Title, string Body)>();
        uint sequence = 0;
        try
        {
            foreach (var request in _requests.GetConsumingEnumerable(_stop.Token))
            {
                if (request.Reset) { last.Remove(request.Window); continue; }
                try
                {
                    if (!_isEnabled(request.Client.Identity)) continue;
                    if (_stop.Token.WaitHandle.WaitOne(120)) return;
                    (string Title, string Body)? content = null;
                    for (var attempt = 0; attempt < 3 && IsPopup(request.Window); attempt++)
                    {
                        content = reader.Read(request.Window, MessengerIdentity.DisplayName(request.Client.Identity));
                        if (content is not null || _stop.Token.WaitHandle.WaitOne(160)) break;
                    }
                    if (content is null || last.GetValueOrDefault(request.Window) == content.Value) continue;
                    last[request.Window] = content.Value;
                    if (last.Count > 128) last.Remove(last.Keys.First());
                    var notification = new IslandNotification(++sequence, request.CreatedAt,
                        MessengerIdentity.DisplayName(request.Client.Identity), content.Value.Title, content.Value.Body,
                        reader.ReadIcon(request.Client.Executable), NotificationSource.ClientHook, request.Client.Identity,
                        $"{request.Client.ProcessId}/{request.Window}/{sequence}");
                    _dispatcher.TryEnqueue(() => { if (!_disposed) Received?.Invoke(notification); });
                }
                catch (Exception) { }
                finally { _dispatcher.TryEnqueue(() => CompleteRead(request)); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void CompleteRead(Request request)
    {
        _queued.Remove(request.Window);
        if (_disposed || !_changed.Remove(request.Window) || !IsPopup(request.Window)) return;
        // Providers can update the same popup while its previous text is being read.
        GetWindowThreadProcessId(request.Window, out var pid);
        if (!_clients.TryGetValue((int)pid, out var client)) return;
        _queued.Add(request.Window);
        if (!_requests.TryAdd(new(request.Window, client, DateTimeOffset.UtcNow))) _queued.Remove(request.Window);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var client in _clients.Values) UnhookWinEvent(client.Hook);
        _clients.Clear();
        _queued.Clear();
        _changed.Clear();
        _stop.Cancel();
        _requests.CompleteAdding();
        _callbackHandle.Free();
        Received = null;
    }

    private delegate void WinEventProc(nint hook, uint eventId, nint hwnd, int objectId, int childId, uint thread, uint time);
    private delegate bool EnumWindowsProc(nint hwnd, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint data);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int count);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
}
