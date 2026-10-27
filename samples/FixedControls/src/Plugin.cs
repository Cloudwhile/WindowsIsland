using WindowsIsland.Contracts;
namespace FixedControls;

public sealed class Plugin : IIslandPlugin
{
    private IPluginContext? _context;
    private CancellationTokenSource? _shutdown;
    private Task? _run;
    public string Id => "wini_plugin_fixedcontrols";
    public string DisplayName => "FixedControls";
    public Task InitializeAsync(IPluginContext context) { _context = context; return Task.CompletedTask; }
    public Task StartAsync()
    {
        if (_run is not null) return Task.CompletedTask;
        var context = _context ?? throw new InvalidOperationException("尚未初始化");
        _shutdown = new CancellationTokenSource();
        _run = RunAsync(context, _shutdown.Token);
        return Task.CompletedTask;
    }
    private async Task RunAsync(IPluginContext context, CancellationToken token)
    {
        try
        {
            await Task.Delay(1000, token);
            if (!context.Settings.GetBoolean("enabled", true)) return;
            token.ThrowIfCancellationRequested();
            var pinned = context.PinnedContent;
            if (pinned is null) return;
            var count = 0;
            var enabled = true;
            var step = 1;
            PinnedContent Content() => new("counter", Id, "计数器", count.ToString(), Controls:
                [new PinnedControl("add", "增加", PinnedControlKind.Button, IsEnabled: enabled, Symbol: "Add"),
                 new PinnedControl("enabled", "允许增加", PinnedControlKind.Toggle, IsOn: enabled),
                 new PinnedControl("step", "每次增加", PinnedControlKind.Slider, Minimum: 1, Maximum: 10, Value: step, Step: 1)]);
            pinned.SetInteractionHandler(interaction =>
            {
                if (token.IsCancellationRequested || interaction.ContentId != "counter") return Task.CompletedTask;
                switch (interaction.ControlId)
                {
                    case "add" when enabled: count += step; break;
                    case "enabled" when interaction.IsOn is { } value: enabled = value; break;
                    case "step" when interaction.Value is >= 1 and <= 10: step = (int)interaction.Value.Value; break;
                    default: return Task.CompletedTask;
                }
                pinned.Update(Content());
                return Task.CompletedTask;
            });
            pinned.Submit(Content());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            context.Diagnostics.Write(new PluginDiagnostic("Plugin", "Run", AdditionalInformation:
                new PluginDiagnosticInfo(error.GetType().FullName, error.HResult)));
        }
    }
    public async Task StopAsync()
    {
        _shutdown?.Cancel();
        try { if (_run is not null) await _run; }
        finally
        {
            _context?.PinnedContent?.SetInteractionHandler(null);
            _context?.PinnedContent?.Remove("counter");
            _shutdown?.Dispose(); _shutdown = null; _run = null;
        }
    }
    public void Dispose()
    {
        // 生命周期要求先 StopAsync；直接释放也取消尚未完成的任务。
        _shutdown?.Cancel(); _shutdown?.Dispose(); _shutdown = null;
        _context?.PinnedContent?.SetInteractionHandler(null);
        _context?.PinnedContent?.Remove("counter");
        _context = null;
    }
}
