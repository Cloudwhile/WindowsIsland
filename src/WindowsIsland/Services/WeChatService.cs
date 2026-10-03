using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace WindowsIsland.Services;

internal sealed class WeChatService : IDisposable
{
    private sealed class Client(int id, string executable)
    {
        public readonly int Id = id;
        public readonly string Executable = executable;
        public bool TriedAccessibility;
        public readonly Dictionary<nint, Watch> Windows = [];
    }
    private sealed class Watch(AutomationElement root, StructureChangedEventHandler callback, AutomationPropertyChangedEventHandler propertyCallback)
    {
        public readonly AutomationElement Root = root;
        public readonly WeChatReader Reader = new(root);
        public readonly WeChatTracker Tracker = new();
        public readonly StructureChangedEventHandler Callback = callback;
        public readonly AutomationPropertyChangedEventHandler PropertyCallback = propertyCallback;
    }

    private readonly Func<bool> _enabled;
    private readonly Action<Action> _dispatch;
    private readonly string? _clientDirectory;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Dictionary<int, Client> _clients = [];
    private readonly WeChatConversationRegistry _conversations = new();
    private readonly object _accessibilityLock = new();
    private readonly Dictionary<int, WeChatAccessibilitySession> _accessibility = [];
    private readonly Thread _thread;
    private int[] _connected = [];
    private string _status = "正在连接";
    private volatile bool _disposed;
    private bool _started;
    public event Action<IslandNotification>? Received;
    public string Status => Volatile.Read(ref _status);
    public bool IsConnected(int id) => Volatile.Read(ref _connected).Contains(id);
    public bool AllowsConversation(string conversation, int? processId = null) => !_disposed && _enabled()
        && _conversations.IsMuted(conversation, processId) is false;

    public WeChatService(Func<bool> enabled, Action<Action> dispatch, string? clientDirectory = null)
    {
        _enabled = enabled;
        _dispatch = dispatch;
        _clientDirectory = clientDirectory is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(clientDirectory)) + Path.DirectorySeparatorChar;
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "WindowsIsland.WeChat.UIAutomation" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        _thread.Start();
    }

    public void Refresh() => Signal();

    private void Signal()
    {
        if (_disposed) return;
        try { _wake.Set(); }
        catch (ObjectDisposedException) { }
    }

    private void ReadLoop()
    {
        var icons = new MessengerPopupReader();
        var clock = Stopwatch.StartNew();
        long scanned = -5000, readAt = -100;
        uint sequence = 0;
        try
        {
            while (!_disposed)
            {
                try
                {
                    if (!_enabled())
                    {
                        ClearClients();
                        Volatile.Write(ref _connected, []);
                        Volatile.Write(ref _status, "已关闭");
                        scanned = -5000;
                        _wake.WaitOne(500);
                        continue;
                    }
                    while (!_disposed && clock.ElapsedMilliseconds - readAt < 100)
                        _wake.WaitOne((int)Math.Max(1, 100 - (clock.ElapsedMilliseconds - readAt)));
                    if (_disposed) break;
                    if (clock.ElapsedMilliseconds - scanned >= 2000)
                    {
                        ScanClients();
                        scanned = clock.ElapsedMilliseconds;
                    }
                    readAt = clock.ElapsedMilliseconds;
                    var connected = new List<int>();
                    foreach (var client in _clients.Values)
                    {
                        foreach (var window in Windows(client.Id))
                        {
                            if (_disposed || !_enabled()) break;
                            try
                            {
                                if (!client.Windows.TryGetValue(window, out var watch))
                                {
                                    var root = AutomationElement.FromHandle(window);
                                    if (root is null) continue;
                                    watch = new(root, (_, _) => Signal(), (_, _) => Signal());
                                    client.Windows[window] = watch;
                                    try { Automation.AddStructureChangedEventHandler(root, TreeScope.Subtree, watch.Callback); }
                                    catch (Exception error) { Trace.WriteLine(error.GetType().Name); }
                                    try
                                    {
                                        Automation.AddAutomationPropertyChangedEventHandler(root, TreeScope.Subtree, watch.PropertyCallback,
                                        AutomationElement.NameProperty, AutomationElement.ItemStatusProperty);
                                    }
                                    catch (Exception error) { Trace.WriteLine(error.GetType().Name); }
                                }
                                var snapshot = watch.Reader.Read();
                                _conversations.Update(client.Id, snapshot.Previews);
                                if (snapshot.ConversationMuted is null)
                                    snapshot = snapshot with { ConversationMuted = _conversations.IsMuted(snapshot.Conversation, client.Id) };
                                if (!snapshot.HasSessions && !snapshot.HasMessages && !client.TriedAccessibility)
                                {
                                    client.TriedAccessibility = true;
                                    RemoveWatch(watch);
                                    client.Windows.Remove(window);
                                    if (EnableAccessibility(client.Id))
                                    {
                                        Signal();
                                        continue;
                                    }
                                }
                                if (!snapshot.HasSessions && !snapshot.HasMessages) continue;
                                connected.Add(client.Id);
                                foreach (var arrival in watch.Tracker.Update(snapshot, DateTimeOffset.Now))
                                {
                                    var notification = new IslandNotification(++sequence, DateTimeOffset.Now, "微信", arrival.Conversation,
                                        arrival.Body, icons.ReadIcon(client.Executable), NotificationSource.ClientAutomation, "wechat",
                                        $"uia/{client.Id}/{window}/{sequence}", OriginProcessId: client.Id);
                                    _dispatch(() => { if (!_disposed && _enabled()) Received?.Invoke(notification); });
                                }
                            }
                            catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                            {
                                if (client.Windows.Remove(window, out var stale)) RemoveWatch(stale);
                                Trace.WriteLine($"WeChat UIAutomation: {error.GetType().Name}");
                            }
                        }
                        foreach (var stale in client.Windows.Keys.Where(window => !IsWindow(window)).ToArray())
                        {
                            RemoveWatch(client.Windows[stale]);
                            client.Windows.Remove(stale);
                        }
                    }
                    Volatile.Write(ref _connected, connected.Distinct().ToArray());
                    Volatile.Write(ref _status, connected.Count > 0 ? "监听中" : _clients.Count > 0 ? "等待会话列表" : "等待客户端");
                    _wake.WaitOne(connected.Count > 0 ? 500 : 1000);
                }
                catch (Exception error)
                {
                    Trace.WriteLine($"WeChat listener: {error.GetType().Name}");
                    ClearClients();
                    Volatile.Write(ref _connected, []);
                    Volatile.Write(ref _status, "正在重新连接");
                    scanned = -5000;
                    if (!_disposed) _wake.WaitOne(1000);
                }
            }
        }
        catch (Exception error) { Trace.WriteLine($"WeChat listener: {error.GetType().Name}"); Volatile.Write(ref _status, "连接暂时不可用"); }
        finally { ClearClients(); Volatile.Write(ref _connected, []); _wake.Dispose(); }
    }

    private void ScanClients()
    {
        var alive = new HashSet<int>();
        foreach (var name in new[] { "Weixin", "WeChat" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        var executable = process.MainModule?.FileName;
                        if (executable is null || _clientDirectory is not null && !executable.StartsWith(_clientDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                        alive.Add(process.Id);
                        _clients.TryAdd(process.Id, new(process.Id, executable));
                    }
                    catch (Exception error) { Trace.WriteLine($"WeChat discovery: {error.GetType().Name}"); }
                }
            }
        }
        foreach (var id in _clients.Keys.Except(alive).ToArray())
        {
            RemoveClient(_clients[id]);
            _clients.Remove(id);
        }
    }

    private bool EnableAccessibility(int id)
    {
        using var process = Process.GetProcessById(id);
        var session = WeChatAccessibilitySession.TryOpen(process);
        if (session is null) return false;
        lock (_accessibilityLock)
        {
            if (_disposed || !_enabled()) { session.Dispose(); return false; }
            _accessibility[id] = session;
        }
        return true;
    }

    private List<nint> Windows(int processId)
    {
        var windows = new List<nint>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var id);
            if (id != processId) return true;
            var className = new StringBuilder(256);
            GetClassName(window, className, className.Capacity);
            var name = className.ToString();
            if (name is "WeChatMainWndForPC" or "ChatWnd" || name.Contains("QWindowIcon", StringComparison.Ordinal)
                && (GetWindowLongPtr(window, -16).ToInt64() & 0x40000) != 0 || _clientDirectory is not null && name.StartsWith("HwndWrapper[", StringComparison.Ordinal))
                windows.Add(window);
            return true;
        }, 0);
        return windows;
    }

    private static void RemoveWatch(Watch watch)
    {
        try { Automation.RemoveStructureChangedEventHandler(watch.Root, watch.Callback); }
        catch (Exception error) { Trace.WriteLine(error.GetType().Name); }
        try { Automation.RemoveAutomationPropertyChangedEventHandler(watch.Root, watch.PropertyCallback); }
        catch (Exception error) { Trace.WriteLine(error.GetType().Name); }
    }

    private void RemoveClient(Client client)
    {
        _conversations.Remove(client.Id);
        lock (_accessibilityLock)
            if (_accessibility.Remove(client.Id, out var session)) session.Dispose();
        foreach (var watch in client.Windows.Values) RemoveWatch(watch);
        client.Windows.Clear();
    }

    private void ClearClients() { foreach (var client in _clients.Values) RemoveClient(client); _clients.Clear(); }

    public void Dispose()
    {
        lock (_accessibilityLock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var session in _accessibility.Values) session.Dispose();
            _accessibility.Clear();
        }
        Received = null;
        _conversations.Clear();
        try { _wake.Set(); } catch (ObjectDisposedException) { }
        if (_started) _thread.Join(1000);
        else _wake.Dispose();
    }

    private delegate bool EnumProc(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint id);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
}
