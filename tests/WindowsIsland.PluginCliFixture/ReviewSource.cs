using WindowsIsland.Contracts;

namespace WindowsIsland.PluginCliFixture;

public sealed class ReviewSource : IIslandPlugin
{
    private IPluginContext? _context;
    public string Id => "wini_plugin_cli_regression";
    public string DisplayName => "CLI regression";
    public Task InitializeAsync(IPluginContext context) { _context = context; return Task.CompletedTask; }
    public Task StartAsync()
    {
        var context = _context!;
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
        }
        return Task.CompletedTask;
    }
    public Task StopAsync() => Task.CompletedTask;
    public void Dispose() { _context = null; }
}
