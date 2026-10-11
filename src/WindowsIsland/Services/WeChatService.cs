using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace WindowsIsland.Services;

internal sealed class WeChatService : IDisposable
{
    private sealed class Client(int id, string executable, long startedAt)
    {
        public readonly int Id = id;
        public readonly string Executable = executable;
        public readonly long StartedAt = startedAt;
        public readonly string? Version = ClientVersion(executable);
        public long NextAccessibilityAttempt;
        public long NextAccessibilityCheck;
        public readonly Dictionary<nint, Watch> Windows = [];
    }
    private sealed class Watch(AutomationElement root, nint window, WeChatAvatarCache avatars,
        StructureChangedEventHandler callback, AutomationPropertyChangedEventHandler propertyCallback)
    {
        public readonly AutomationElement Root = root;
        public readonly WeChatReader Reader = new(root, window, avatars);
        public readonly WeChatTracker Tracker = new();
        public readonly WeChatAvatarEnrichment AvatarEnrichment = new();
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
    private readonly WeChatConnectionHealth _health = new();
    private int[] _connected = [];
    private string _status = "NotConnected";
    private int _retry;
    private volatile bool _disposed;
    private bool _started;
    public event Action<IslandNotification>? Received;
    public string StatusKey => Volatile.Read(ref _status);
    public string Status => Localization.Get(StatusKey);
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

    // Allows a degraded client one immediate accessibility attempt from settings.
    public void Retry()
    {
        Interlocked.Exchange(ref _retry, 1);
        Signal();
    }

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
                        Volatile.Write(ref _status, "Off");
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
                    if (Interlocked.Exchange(ref _retry, 0) != 0)
                    {
                        _health.Retry();
                        foreach (var client in _clients.Values) client.NextAccessibilityAttempt = 0;
                    }
                    var connected = new List<int>();
                    foreach (var client in _clients.Values)
                    {
                        if (clock.ElapsedMilliseconds >= client.NextAccessibilityCheck)
                        {
                            client.NextAccessibilityCheck = clock.ElapsedMilliseconds + 5000;
                            MaintainAccessibility(client);
                        }
                        foreach (var window in Windows(client.Id))
                        {
                            if (_disposed || !_enabled()) break;
                            try
                            {
                                if (!client.Windows.TryGetValue(window, out var watch))
                                {
                                    var root = AutomationElement.FromHandle(window);
                                    if (root is null) continue;
                                    var avatarDirectory = _clientDirectory is null ? Path.Combine(
                                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsIsland", "Cache", "WeChatAvatars") : null;
                                    var avatars = new WeChatAvatarCache(avatarDirectory, $"{client.Id}/{client.StartedAt}/{window}");
                                    watch = new(root, window, avatars, (_, _) => Signal(), (_, _) => Signal());
                                    client.Windows[window] = watch;
                                    try { Automation.AddStructureChangedEventHandler(root, TreeScope.Subtree, watch.Callback); }
                                    catch (Exception error) { Diagnostics.Capture(DiagnosticArea.WeChat, error); Trace.WriteLine(error.GetType().Name); }
                                    try
                                    {
                                        Automation.AddAutomationPropertyChangedEventHandler(root, TreeScope.Subtree, watch.PropertyCallback,
                                        AutomationElement.NameProperty, AutomationElement.ItemStatusProperty);
                                    }
                                    catch (Exception error) { Diagnostics.Capture(DiagnosticArea.WeChat, error); Trace.WriteLine(error.GetType().Name); }
                                }
                                var snapshot = watch.Reader.Read();
                                _conversations.Update(client.Id, snapshot.Previews);
                                if (snapshot.ConversationMuted is null)
                                    snapshot = snapshot with { ConversationMuted = _conversations.IsMuted(snapshot.Conversation, client.Id) };
                                if (!snapshot.HasSessions && !snapshot.HasMessages && clock.ElapsedMilliseconds >= client.NextAccessibilityAttempt)
                                {
                                    client.NextAccessibilityAttempt = clock.ElapsedMilliseconds + 10000;
                                    RemoveWatch(watch);
                                    client.Windows.Remove(window);
                                    if (EnableAccessibility(client, out var failure))
                                    {
                                        Diagnostics.State(DiagnosticArea.WeChat, "AccessibilityEnabled", client.Version);
                                        Signal();
                                    }
                                    else
                                    {
                                        _health.Failed(client.Id, WeChatConnectionHealth.IsUnsupported(failure));
                                        Diagnostics.State(DiagnosticArea.WeChat, "AccessibilityUnavailable", client.Version);
                                    }
                                    continue;
                                }
                                if (!snapshot.HasSessions && !snapshot.HasMessages) continue;
                                connected.Add(client.Id);
                                _health.Succeeded(client.Id);
                                foreach (var arrival in watch.Tracker.Update(snapshot, DateTimeOffset.Now))
                                {
                                    var notification = new IslandNotification(++sequence, DateTimeOffset.Now, "微信", arrival.Conversation,
                                        arrival.Body, icons.ReadIcon(client.Executable), NotificationSource.ClientAutomation, "wechat",
                                        $"uia/{client.Id}/{window}/{sequence}", SenderAvatar: arrival.Avatar, OriginProcessId: client.Id);
                                    watch.AvatarEnrichment.Remember(notification, snapshot.Previews, DateTimeOffset.UtcNow);
                                    _dispatch(() => { if (!_disposed && _enabled()) Received?.Invoke(notification); });
                                }
                                foreach (var update in watch.AvatarEnrichment.Update(snapshot.Previews, DateTimeOffset.UtcNow))
                                    _dispatch(() => { if (!_disposed && _enabled()) Received?.Invoke(update); });
                            }
                            catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                            {
                                if (client.Windows.Remove(window, out var stale)) RemoveWatch(stale);
                                Diagnostics.Capture(DiagnosticArea.WeChat, error); Trace.WriteLine($"WeChat UIAutomation: {error.GetType().Name}");
                            }
                        }
                        foreach (var stale in client.Windows.Keys.Where(window => !IsWindow(window)).ToArray())
                        {
                            RemoveWatch(client.Windows[stale]);
                            client.Windows.Remove(stale);
                        }
                    }
                    Volatile.Write(ref _connected, connected.Distinct().ToArray());
                    Volatile.Write(ref _status, _health.StatusKey(connected.Count > 0, _clients.Count));
                    _wake.WaitOne(connected.Count > 0 ? 500 : 1000);
                }
                catch (Exception error)
                {
                    Diagnostics.Capture(DiagnosticArea.WeChat, error); Trace.WriteLine($"WeChat listener: {error.GetType().Name}");
                    ClearClients();
                    Volatile.Write(ref _connected, []);
                    Volatile.Write(ref _status, "NotConnected");
                    scanned = -5000;
                    if (!_disposed) _wake.WaitOne(1000);
                }
            }
        }
        catch (Exception error) { Diagnostics.Capture(DiagnosticArea.WeChat, error); Trace.WriteLine($"WeChat listener: {error.GetType().Name}"); Volatile.Write(ref _status, "WeChatConnectFailed"); }
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
                        _clients.TryAdd(process.Id, new(process.Id, executable, process.StartTime.ToUniversalTime().Ticks));
                    }
                    catch (Exception error) { Diagnostics.Capture(DiagnosticArea.WeChat, error); Trace.WriteLine($"WeChat discovery: {error.GetType().Name}"); }
                }
            }
        }
        foreach (var id in _clients.Keys.Except(alive).ToArray())
        {
            RemoveClient(_clients[id]);
            _clients.Remove(id);
        }
    }

    private bool EnableAccessibility(Client client, out string failure)
    {
        failure = nameof(WeChatAccessibilityFailure.None);
        lock (_accessibilityLock)
            if (_accessibility.TryGetValue(client.Id, out var active)) return active.EnsureEnabled(out _);
        using var process = Process.GetProcessById(client.Id);
        var session = WeChatAccessibilitySession.TryOpen(process, out var reason);
        if (session is null)
        {
            failure = reason.ToString();
            Diagnostics.State(DiagnosticArea.WeChat, "Accessibility." + failure, client.Version);
            return false;
        }
        lock (_accessibilityLock)
        {
            if (_disposed || !_enabled()) { session.Dispose(); return false; }
            _accessibility[client.Id] = session;
        }
        return true;
    }

    private void MaintainAccessibility(Client client)
    {
        var reset = false;
        lock (_accessibilityLock)
        {
            if (!_accessibility.TryGetValue(client.Id, out var session)) return;
            if (!session.EnsureEnabled(out var restored))
            {
                _accessibility.Remove(client.Id);
                session.Dispose();
                reset = true;
                _health.Failed(client.Id, unsupported: false);
                Diagnostics.State(DiagnosticArea.WeChat, "AccessibilityLost", client.Version);
            }
            else if (restored)
            {
                reset = true;
                Diagnostics.State(DiagnosticArea.WeChat, "AccessibilityRestored", client.Version);
            }
        }
        if (!reset) return;
        // A gap in accessibility is a reconnect: establish a new silent baseline.
        foreach (var watch in client.Windows.Values) RemoveWatch(watch);
        client.Windows.Clear();
        _conversations.Remove(client.Id);
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
        watch.Reader.Dispose();
        try { Automation.RemoveStructureChangedEventHandler(watch.Root, watch.Callback); }
        catch (Exception error) { Diagnostics.Capture(DiagnosticArea.WeChat, error); Trace.WriteLine(error.GetType().Name); }
        try { Automation.RemoveAutomationPropertyChangedEventHandler(watch.Root, watch.PropertyCallback); }
        catch (Exception error) { Diagnostics.Capture(DiagnosticArea.WeChat, error); Trace.WriteLine(error.GetType().Name); }
    }

    private void RemoveClient(Client client)
    {
        _conversations.Remove(client.Id);
        _health.Remove(client.Id);
        lock (_accessibilityLock)
            if (_accessibility.Remove(client.Id, out var session)) session.Dispose();
        foreach (var watch in client.Windows.Values) RemoveWatch(watch);
        client.Windows.Clear();
    }

    private void ClearClients() { foreach (var client in _clients.Values) RemoveClient(client); _clients.Clear(); }

    // Only the numeric file version is kept for diagnostics, never the install path.
    private static string? ClientVersion(string executable)
    {
        try { return DiagnosticReports.ClientVersion(FileVersionInfo.GetVersionInfo(executable).FileVersion); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

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
