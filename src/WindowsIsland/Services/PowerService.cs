using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace WindowsIsland.Services;

internal sealed class PowerService : IDisposable
{
    private readonly nint _window;
    private readonly DispatcherQueue _dispatcher;
    private readonly PowerTracker _tracker;
    private readonly SubclassProc _callback;
    private readonly DispatcherQueueTimer _broadcastDebounce;
    private readonly DispatcherQueueTimer _negotiationHold;
    private readonly List<nint> _registrations = [];
    private bool _wasConnected;
    private bool _disposed;
    public event Action<IslandNotification>? Received;

    public PowerService(nint window, DispatcherQueue dispatcher, PowerQuotaSettings? quota = null)
    {
        _window = window;
        _dispatcher = dispatcher;
        _tracker = new PowerTracker(quota);
        _callback = WindowProc;
        _broadcastDebounce = _dispatcher.CreateTimer();
        _broadcastDebounce.Interval = TimeSpan.FromMilliseconds(180);
        _broadcastDebounce.IsRepeating = false;
        _broadcastDebounce.Tick += (_, _) => PollCore(allowHold: true);
        _negotiationHold = _dispatcher.CreateTimer();
        _negotiationHold.Interval = TimeSpan.FromMilliseconds(420);
        _negotiationHold.IsRepeating = false;
        _negotiationHold.Tick += (_, _) => PollCore(allowHold: false);
        if (!SetWindowSubclass(window, _callback, 2, 0)) throw new Win32Exception();
        foreach (var id in new[] { "5D3E9A59-E9D5-4B00-A6BD-FF34FF516548", "A7AD8041-B45A-4CAE-87A3-EECBB468A9E1" })
        {
            var guid = new Guid(id);
            var registration = RegisterPowerSettingNotification(window, ref guid, 0);
            if (registration != 0) _registrations.Add(registration);
        }
        PollCore(allowHold: false);
    }

    public void ConfigureQuota(PowerQuotaSettings quota)
    {
        if (!_disposed) _tracker.ConfigureQuota(quota);
    }

    public void Poll() => PollCore(allowHold: true);

    private void PollCore(bool allowHold)
    {
        if (_disposed || !GetSystemPowerStatus(out var status) || status.ACLineStatus == 255 || status.BatteryFlag == 255) return;
        var snapshot = new PowerSnapshot((status.BatteryFlag & 128) == 0, status.ACLineStatus == 1,
            (status.BatteryFlag & 8) != 0, status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : null);
        // 刚接入外接电源但 EC 尚未建立充电握手的短暂窗口（420ms）先等待充电位就绪，避免快充协议协商导致连续弹出「已连接电源」与「开始充电」。
        if (allowHold && !_wasConnected && snapshot.HasBattery && snapshot.Connected
            && !snapshot.Charging && snapshot.Percent is null or < 100)
        {
            if (!_negotiationHold.IsRunning) _negotiationHold.Start();
            return;
        }
        _negotiationHold.Stop();
        _wasConnected = snapshot.HasBattery && snapshot.Connected;
        var notification = _tracker.Update(snapshot);
        if (notification is not null) Received?.Invoke(notification);
    }

    private nint WindowProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        // Coalesce bursty AC/DC & EC charger-negotiation broadcasts and step past the OS power-switch spike.
        if (!_disposed && message == 0x0218 && wParam is 0x000A or 0x0007 or 0x0012 or 0x8013)
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (_disposed) return;
                _broadcastDebounce.Stop();
                _broadcastDebounce.Start();
            });
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _broadcastDebounce.Stop();
        _negotiationHold.Stop();
        foreach (var registration in _registrations) UnregisterPowerSettingNotification(registration);
        RemoveWindowSubclass(_window, _callback, 2);
        Received = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    [DllImport("user32.dll")] private static extern nint RegisterPowerSettingNotification(nint recipient, ref Guid setting, uint flags);
    [DllImport("user32.dll")] private static extern bool UnregisterPowerSettingNotification(nint registration);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
}
