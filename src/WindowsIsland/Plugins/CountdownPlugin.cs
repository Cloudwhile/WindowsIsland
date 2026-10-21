using System.Globalization;
using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

internal sealed class CountdownPlugin(TimeProvider? time = null, Func<string>? title = null, Func<string, string>? label = null)
    : IMessageSource, IPluginPolling, IPluginStatusProvider, IPluginRetry
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private IPluginContext? _context;
    private DateTimeOffset? _deadline;
    private bool _started;
    private readonly object _gate = new();
    private bool _paused;
    private TimeSpan _remaining;
    private int _duration = 300;
    public string Id => BuiltInPluginIds.Countdown;
    public string DisplayName => title?.Invoke() ?? "倒计时";
    public event Action<PluginNotification>? NotificationReceived { add { } remove { } }
    public PluginStatus Status => new(PluginConnectionState.Connected);
    public bool CanRetry => _started;

    public Task InitializeAsync(IPluginContext context)
    {
        _context = context;
        context.PinnedContent?.SetInteractionHandler(InteractAsync);
        return Task.CompletedTask;
    }
    public Task StartAsync()
    {
        lock (_gate) if (!_started) { _started = true; Restart(); }
        return Task.CompletedTask;
    }
    public void Retry() { lock (_gate) if (_started) Restart(); }
    private void Restart()
    {
        if (_context is null) return;
        var configured = _context.Settings.GetString("DurationSeconds", "300");
        _duration = int.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds is >= 1 and <= 86400 ? seconds : 300;
        _paused = false;
        _deadline = _time.GetUtcNow().AddSeconds(_duration);
        _context.PinnedContent?.Submit(Snapshot());
    }
    private PinnedContent Snapshot()
    {
        var seconds = Math.Max(0, (int)Math.Ceiling((_paused ? _remaining : _deadline!.Value - _time.GetUtcNow()).TotalSeconds));
        var text = seconds >= 3600 ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
            : $"{seconds / 60:00}:{seconds % 60:00}";
        return new("countdown", Id, DisplayName, text, ExpiresAt: _paused ? null : _deadline, Symbol: "timer",
            Controls: [new("restart", Label("ActionRestartCountdown", "重新开始倒计时"), PinnedControlKind.Button, Symbol: "refresh"),
                new("pause", Label("CountdownPause", "暂停"), PinnedControlKind.Toggle, IsOn: _paused),
                new("duration", Label("CountdownDuration", "时长（秒）"), PinnedControlKind.Slider,
                    Minimum: 1, Maximum: 86400, Value: _duration, Step: 1)]);
    }
    private string Label(string key, string fallback) => label?.Invoke(key) ?? fallback;
    private Task InteractAsync(PinnedControlInteraction interaction)
    {
        lock (_gate)
        {
            if (!_started) return Task.CompletedTask;
            switch (interaction.ControlId)
            {
                case "restart": Restart(); break;
                case "duration" when interaction.Value is { } value:
                    _context!.Settings.SetString("DurationSeconds", ((int)value).ToString(CultureInfo.InvariantCulture));
                    Restart(); break;
                case "pause" when interaction.IsOn is { } paused && paused != _paused && (_deadline is not null || _paused):
                    if (paused) _remaining = _deadline!.Value - _time.GetUtcNow();
                    else _deadline = _time.GetUtcNow() + _remaining;
                    _paused = paused;
                    _context!.PinnedContent?.Update(Snapshot());
                    break;
            }
        }
        return Task.CompletedTask;
    }
    public Task PollAsync()
    {
        lock (_gate)
        {
            if (!_started || _deadline is null || _paused) return Task.CompletedTask;
            if (_time.GetUtcNow() >= _deadline)
            {
                _context?.PinnedContent?.Remove("countdown");
                _deadline = null;
            }
            else _context?.PinnedContent?.Update(Snapshot());
        }
        return Task.CompletedTask;
    }
    public Task StopAsync()
    {
        lock (_gate)
        {
            _started = false;
            _deadline = null;
            _context?.PinnedContent?.Remove("countdown");
        }
        return Task.CompletedTask;
    }
    public void Dispose() { _ = StopAsync(); _context = null; }
}
