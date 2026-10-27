using WindowsIsland.Contracts;

namespace WindowsIsland.PluginCliFixture;

public sealed class ReviewSource : IIslandPlugin
{
    private IPluginContext? _context;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _delayed;
    public string Id => "wini_plugin_cli_regression";
    public string DisplayName => "CLI regression";
    public Task InitializeAsync(IPluginContext context) { _context = context; return Task.CompletedTask; }
    public Task StartAsync()
    {
        var context = _context!;
        if (context.GetService<IPluginPermissions>()?.IsGranted("custom_capability") == true)
            throw new InvalidOperationException("A label must not grant a system capability.");
        switch (context.Settings.GetString("mode"))
        {
            case "wrong-source":
                context.PublishNotification(new("wini_plugin_someone_else", "Fixture", null, "Synthetic", DateTimeOffset.UtcNow));
                break;
            case "no-permission":
                context.PublishNotification(new(Id, "Fixture", null, "Synthetic", DateTimeOffset.UtcNow));
                break;
            case "invalid-pinned":
                context.PinnedContent?.Submit(new("pin", "wini_plugin_someone_else", "Fixture", "0"));
                break;
            case "throw-start": throw new InvalidOperationException("Synthetic start failure.");
            case "valid": context.PublishNotification(new(Id, "Fixture", null, "Synthetic", DateTimeOffset.UtcNow)); break;
            case "delayed":
                _delayed = Task.Run(async () =>
                {
                    await Task.Delay(2200, _lifetime.Token);
                    context.PublishNotification(new(Id, "Fixture", null, "Synthetic", DateTimeOffset.UtcNow));
                });
                break;
        }
        return Task.CompletedTask;
    }
    public async Task StopAsync()
    {
        _lifetime.Cancel();
        try { if (_delayed is not null) await _delayed; }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); _context = null; }
}
