using WindowsIsland.Contracts;

namespace WindowsIsland.PluginSecurityFixture;

public sealed class PeerSource : IIslandPlugin
{
    public string Id => "wini_plugin_security_peer";
    public string DisplayName => "Security peer fixture";
    public Task InitializeAsync(IPluginContext context) => Task.CompletedTask;
    public Task StartAsync() => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public void Dispose() { }
}
