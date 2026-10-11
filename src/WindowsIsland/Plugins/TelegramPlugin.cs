using System.Diagnostics;
using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

internal sealed class TelegramPlugin(BuiltInPluginContext context) : NotificationPlugin
{
    private TelegramNativeService? _native;
    private MessengerHookService? _popups;
    private int _ticks;
    public override string StatusKey => _popups?.ConnectedApps.Contains("telegram") == true ? "Connected" : "TelegramNotDetected";

    public override Task StartAsync()
    {
        if (IsDisposed || _native is not null) return Task.CompletedTask;
        _native = new TelegramNativeService(context.Dispatcher, context.Verification);
        _native.Received += Publish;
        _popups = new MessengerHookService(context.Dispatcher, context.FixtureDirectory("WindowsIsland.HookFixture"),
            identity => !IsDisposed && identity == "telegram");
        _popups.NativeTelegramConnected = _native.IsConnected;
        _popups.Received += Publish;
        _popups.Start();
        _native.RefreshClients(Clients());
        return Task.CompletedTask;
    }

    public override Task PollAsync()
    {
        if (!IsDisposed && ++_ticks % 5 == 0)
        {
            _popups?.RefreshClients();
            _native?.RefreshClients(Clients());
        }
        return Task.CompletedTask;
    }

    private int[] Clients()
    {
        if (!context.Verification) return _popups?.TelegramProcessIds ?? [];
        var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "native", "Release", "WindowsIsland.TelegramFixture.exe"));
        var clients = new List<int>();
        foreach (var process in Process.GetProcessesByName("WindowsIsland.TelegramFixture"))
        {
            using (process)
            {
                try { if (string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) clients.Add(process.Id); }
                catch (Exception) { }
            }
        }
        return clients.ToArray();
    }

    protected override void DisposeCore()
    {
        try { if (_popups is not null) { _popups.Received -= Publish; _popups.Dispose(); } }
        finally { if (_native is not null) { _native.Received -= Publish; _native.Dispose(); } }
    }
}
