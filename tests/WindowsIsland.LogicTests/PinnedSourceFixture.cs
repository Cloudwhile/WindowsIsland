using System.Globalization;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

// 仅用于验证宿主通道和生命周期，不随应用发布。
internal sealed class PinnedSourceFixture(TimeProvider time, int initialValue = 12)
    : IMessageSource, IPluginPolling, IPluginRetry
{
    public const string PluginId = "wini_pinned_fixture_plugin";
    public const string ContentId = "sample";
    public static readonly NotificationPluginDefinition Definition = new(PluginId, "Fixture", "FixtureToggle", 0,
        DiagnosticArea.Application, settings => settings.Power, (settings, enabled) => settings with { Power = enabled },
        _ => false, PluginMetadata.BuiltIn);
    private IPluginContext? _context;
    private DateTimeOffset _updated;
    private bool _paused, _started;
    private int _value, _duration;
    public string Id => PluginId;
    public string DisplayName => "固定内容夹具";
    public event Action<PluginNotification>? NotificationReceived { add { } remove { } }
    public bool CanRetry => _started;

    public Task InitializeAsync(IPluginContext context)
    {
        _context = context;
        context.PinnedContent!.SetInteractionHandler(InteractAsync);
        return Task.CompletedTask;
    }
    public Task StartAsync() { _started = true; Reset(); return Task.CompletedTask; }
    public void Retry() => Reset();
    private void Reset()
    {
        _paused = false;
        _value = 0;
        _duration = initialValue;
        _updated = time.GetUtcNow();
        _context!.PinnedContent!.Submit(Snapshot());
    }
    private PinnedContent Snapshot() => new(ContentId, Id, DisplayName, _value.ToString(CultureInfo.InvariantCulture),
        Body: "固定详情", Controls: [new("restart", "重置", PinnedControlKind.Button),
            new("pause", "暂停", PinnedControlKind.Toggle, IsOn: _paused),
            new("duration", "采样间隔", PinnedControlKind.Slider, Minimum: 1, Maximum: 86400, Value: _duration, Step: 1)]);

    private Task InteractAsync(PinnedControlInteraction interaction)
    {
        switch (interaction.ControlId)
        {
            case "restart": Reset(); return Task.CompletedTask;
            case "pause": _paused = interaction.IsOn == true; _updated = time.GetUtcNow(); break;
            case "duration":
                _duration = (int)interaction.Value!.Value;
                _context!.Settings.SetString("SampleInterval", _duration.ToString(CultureInfo.InvariantCulture));
                break;
        }
        _context!.PinnedContent!.Update(Snapshot());
        return Task.CompletedTask;
    }
    public Task PollAsync()
    {
        var now = time.GetUtcNow();
        if (_started && !_paused) _value += Math.Max(0, (int)(now - _updated).TotalSeconds);
        _updated = now;
        if (_started) _context!.PinnedContent!.Update(Snapshot());
        return Task.CompletedTask;
    }
    public void Remove() => _context!.PinnedContent!.Remove(ContentId);
    public Task StopAsync() { _started = false; return Task.CompletedTask; }
    public void Dispose() { _started = false; _context = null; }
}
