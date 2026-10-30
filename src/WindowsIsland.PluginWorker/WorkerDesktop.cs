using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.PluginIsolation;

namespace WindowsIsland.PluginWorker;

internal sealed class WorkerDesktop(IsolationRpc rpc, CancellationToken lifetime) : IPluginDesktop
{
    public Task<bool> OpenBridgeAsync(DesktopBridge request, CancellationToken token = default) => CallAsync<bool>("bridgeOpen", request, token);
    public Task<DesktopBridgeData> ReadBridgeAsync(int processId, CancellationToken token = default) => CallAsync<DesktopBridgeData>("bridgeRead", processId, token);
    public Task<bool> CloseBridgeAsync(int processId, CancellationToken token = default) => CallAsync<bool>("bridgeClose", processId, token);
    private async Task<T> CallAsync<T>(string operation, object? payload, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        linked.CancelAfter(TimeSpan.FromSeconds(15));
        return await rpc.CallAsync<T>("desktop", new WorkerDesktopRequest(operation,
            JsonSerializer.SerializeToElement(payload, IsolationRpc.Json)), linked.Token).ConfigureAwait(false)
            ?? throw new IOException("Desktop service is unavailable.");
    }
    public Task<DesktopClient[]> FindClientsAsync(CancellationToken token = default) => CallAsync<DesktopClient[]>("clients", null, token);
    public Task<DesktopTree> ReadTreeAsync(int processId, long window, CancellationToken token = default) =>
        CallAsync<DesktopTree>("tree", new DesktopWindowRequest(processId, window), token);
    public Task<byte[]> ReadModuleAsync(DesktopRead request, CancellationToken token = default) => CallAsync<byte[]>("module", request, token);
    public Task<byte[]> ReadMemoryAsync(DesktopRead request, CancellationToken token = default) => CallAsync<byte[]>("memory", request, token);
    public Task<bool> SetFlagAsync(DesktopFlag request, CancellationToken token = default) => CallAsync<bool>("flag", request, token);
    public async Task<byte[]?> ReadImageAsync(DesktopImage request, CancellationToken token = default) =>
        await CallAsync<byte[]>("image", request, token).ConfigureAwait(false) is { Length: > 0 } bytes ? bytes : null;
}
