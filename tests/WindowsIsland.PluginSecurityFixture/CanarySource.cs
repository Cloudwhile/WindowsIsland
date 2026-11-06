using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using WindowsIsland.Contracts;

namespace WindowsIsland.PluginSecurityFixture;

[SupportedOSPlatform("windows")]
public sealed class CanarySource : IMessageSource, IPluginSettingsSchema, IPluginStatusProvider,
    IPluginLifecycleAware, IPluginPolling, IPluginRetry
{
    private IPluginContext? _context;
    private CanaryPlan? _plan;
    private CanaryResult? _result;
    private bool _running;
    private int _pollCount, _retryCount, _interactionCount;
    public string Id => "wini_plugin_security_fixture";
    public string DisplayName => "Security canary fixture";
    public bool CanRetry => true;
    public PluginStatus Status => new(_running ? PluginConnectionState.Connected : PluginConnectionState.NotRunning);
    public event Action<PluginNotification>? NotificationReceived;
    public IReadOnlyList<PluginSetting> SettingsSchema =>
    [
        new("fixtureText", PluginSettingType.String, "Fixture string", "canary-default"),
        new("fixtureBoolean", PluginSettingType.Boolean, "Fixture boolean", true)
    ];

    public CanarySource()
    {
        // 条款未接受时宿主应完全不构造此类型；仅在夹具自身可写目录留下标记。
        var plan = CanaryPlan.FromCodeDirectory();
        if (plan is not null)
            try { File.WriteAllText(Path.Combine(plan.OwnData, "constructor.marker"), "constructed"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public Task InitializeAsync(IPluginContext context)
    {
        _context = context;
        _plan = CanaryPlan.Read(context.Settings.GetString(CanaryPlan.SettingsKey)) ?? CanaryPlan.FromCodeDirectory();
        context.Settings.SetString("fixtureText", "canary-round-trip");
        context.Settings.SetBoolean("fixtureBoolean", true);
        var settings = context.Settings.GetString("fixtureText") == "canary-round-trip"
            && context.Settings.GetBoolean("fixtureBoolean");
        if (_plan is not null)
        {
            var own = context.GetService<IPluginStorage>()?.DataDirectory;
            if (!string.Equals(Path.GetFullPath(own ?? ""), _plan.OwnData, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid canary storage.");
        }
        _result = new(false, false, false, false, false, false, false, false, false,
            false, false, context.GetService<IPluginNetwork>() is not null, false, 0, settings,
            Environment.GetEnvironmentVariable(CanaryPlan.SecretEnvironmentKey) is not null, false, ReadTokenSid(), 0, 0, 0);
        context.PinnedContent?.SetInteractionHandler(async interaction =>
        {
            _interactionCount++;
            if (interaction.ControlId == "fixture-upload") await ProbeBrokerAsync();
            SaveResult();
        });
        return Task.CompletedTask;
    }

    public async Task StartAsync()
    {
        try { await StartCanaryAsync(); }
        catch (Exception error)
        {
            if (_plan is not null) File.WriteAllText(Path.Combine(_plan.OwnData, "fixture-start-error.txt"), error.ToString());
            throw;
        }
    }

    private async Task StartCanaryAsync()
    {
        if (_plan?.ThrowOnStart == true)
            throw new InvalidOperationException("SECURITY_FIXTURE_PRIVATE_EXCEPTION_MESSAGE");
        _running = true;
        var context = _context ?? throw new InvalidOperationException("Fixture is not initialized.");
        if (_plan is { } plan && _result is { } result)
        {
            var ownFile = Path.Combine(plan.OwnData, "own-canary.txt");
            File.WriteAllText(ownFile, "fixed-own-canary");
            var own = TryWrite(ownFile) && TryRead(ownFile);
            var directHttp = false;
            var directSocket = false;
            if (plan.LoopbackPort != 0)
            {
                directHttp = await ProbeHttpAsync(plan.LoopbackPort);
                directSocket = await ProbeSocketAsync(plan.LoopbackPort);
            }
            var brokerSucceeded = false;
            var brokerStatus = 0;
            if (plan.ProbeBroker && context.GetService<IPluginNetwork>() is { } network)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    var response = await network.SendAsync(new(CanaryPlan.BrokerUrl, "POST", "Fixed isolated canary upload",
                        Body: Encoding.UTF8.GetBytes("fixed-isolated-canary")), timeout.Token);
                    brokerStatus = response.StatusCode;
                    brokerSucceeded = response.StatusCode is >= 200 and < 300;
                }
                catch { }
            }
            _result = result with
            {
                OwnReadWrite = own,
                HostReadable = TryRead(plan.HostCanary), HostWritable = TryWrite(plan.HostCanary),
                OtherPluginReadable = TryRead(plan.OtherPluginCanary), OtherPluginWritable = TryWrite(plan.OtherPluginCanary),
                ReceiptReadable = TryRead(plan.ReceiptCanary), ReceiptWritable = TryWrite(plan.ReceiptCanary),
                NativeHostReadable = NativeRead(plan.HostCanary), HostProcessAccessible = NativeOpenProcess(plan.HostProcessId),
                DirectHttpSucceeded = directHttp, DirectSocketSucceeded = directSocket,
                BrokerSucceeded = brokerSucceeded, BrokerStatusCode = brokerStatus,
                ChildProcessCreated = await ProbeChildProcessAsync(),
                HostWindowReadable = CanReadWindow(plan.HostWindow),
                OtherPluginCodeReadable = TryRead(plan.OtherPluginCode),
                HostAclWritable = NativeRead(plan.HostCanary, 0x00040000),
                PeerAclWritable = NativeRead(plan.OtherPluginCanary, 0x00040000),
                ReceiptAclWritable = NativeRead(plan.ReceiptCanary, 0x00040000),
                HostHandlesAccessible = NativeOpenProcess(plan.HostProcessId, 0x0040)
            };
        }
        context.Diagnostics.Write(new("plugin", "SecurityFixtureStart"));
        context.PinnedContent?.Submit(new("fixture-pin", Id, "Canary", "1",
            Controls: [new("fixture-action", "Canary action", PinnedControlKind.Button),
                new("fixture-upload", "Canary upload", PinnedControlKind.Button)]));
        context.PublishNotification(new(Id, "Canary context", null, "fixed-canary", DateTimeOffset.UtcNow));
        NotificationReceived?.Invoke(new(Id, "Canary event", null, "fixed-canary-event", DateTimeOffset.UtcNow));
        SaveResult();
    }

    public Task PollAsync() { _pollCount++; SaveResult(); return Task.CompletedTask; }
    public void Retry() { _retryCount++; SaveResult(); }
    public Task OnStoppingAsync(PluginStopReason reason)
    {
        // 仅写自身夹具结果，验证已撤销的设置能力不再改变宿主存储。
        _context?.Settings.SetString("revokedProbe", "must-not-be-persisted");
        _running = false;
        return Task.CompletedTask;
    }
    public Task StopAsync() { _running = false; return Task.CompletedTask; }
    public Task OnUninstallAsync()
    {
        if (_plan is not null) File.WriteAllText(Path.Combine(_plan.OwnData, "uninstall.marker"), "uninstalled");
        return Task.CompletedTask;
    }
    public void Dispose() { _running = false; }

    private async Task ProbeBrokerAsync()
    {
        if (_result is null || _context?.GetService<IPluginNetwork>() is not { } network) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var response = await network.SendAsync(new(CanaryPlan.BrokerUrl, "POST", "Fixed isolated canary upload",
                Body: Encoding.UTF8.GetBytes("fixed-isolated-canary")), timeout.Token);
            _result = _result with { BrokerSucceeded = response.StatusCode is >= 200 and < 300, BrokerStatusCode = response.StatusCode };
        }
        catch { _result = _result with { BrokerSucceeded = false, BrokerStatusCode = 0 }; }
    }

    private void SaveResult()
    {
        if (_result is null || _context is null) return;
        _result = _result with { PollCount = _pollCount, RetryCount = _retryCount, InteractionCount = _interactionCount };
        var json = JsonSerializer.Serialize(_result);
        _context.Settings.SetString(CanaryPlan.ResultKey, json);
        if (_plan is not null) File.WriteAllText(Path.Combine(_plan.OwnData, "probe-result.json"), json);
    }

    private static bool TryRead(string path)
    {
        try { using var file = File.OpenRead(path); return file.ReadByte() >= 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool CanReadWindow(long handle)
    {
        if (handle == 0) return false;
        var text = new StringBuilder(128);
        return GetWindowText((nint)handle, text, text.Capacity) > 0 && text.ToString() == "fixed-host-private-window-canary";
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(nint window, StringBuilder text, int size);

    private static bool TryWrite(string path)
    {
        try { using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); file.WriteByte(0x43); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private static async Task<bool> ProbeHttpAsync(int port)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            using var response = await client.PostAsync($"http://127.0.0.1:{port}/canary", new StringContent("fixed-isolated-canary"));
            return true;
        }
        catch { return false; }
    }

    private static async Task<bool> ProbeSocketAsync(int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            return true;
        }
        catch { return false; }
    }

    private static bool NativeRead(string path, uint access = 0x80000000)
    {
        var handle = CreateFile(path, access, 7, 0, 3, 0, 0);
        if (handle == new nint(-1)) return false;
        CloseHandle(handle);
        return true;
    }

    private static bool NativeOpenProcess(int processId, uint access = 0x0010)
    {
        if (processId == 0 || processId == Environment.ProcessId) return false;
        var handle = OpenProcess(access, false, processId); // 分别检查内存读取与句柄复制权限。
        if (handle == 0) return false;
        CloseHandle(handle);
        return true;
    }

    private static async Task<bool> ProbeChildProcessAsync()
    {
        try
        {
            using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                ArgumentList = { "--security-fixture-noop" } });
            if (child is null) return false;
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { child.Kill(); }
            return true;
        }
        catch { return false; }
    }

    private static string? ReadTokenSid()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 8, out var token)) return null;
        try
        {
            GetTokenInformation(token, 31, 0, 0, out var size);
            if (size is 0 or > 4096) return null;
            var memory = Marshal.AllocHGlobal((int)size);
            try
            {
                return GetTokenInformation(token, 31, memory, size, out _) && Marshal.ReadIntPtr(memory) != 0
                    ? new SecurityIdentifier(Marshal.ReadIntPtr(memory)).Value : null;
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        finally { CloseHandle(token); }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(nint token, int kind, nint memory, uint size, out uint needed);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
