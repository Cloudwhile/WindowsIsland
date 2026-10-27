using WindowsIsland.Contracts;
using WindowsIsland.PluginIsolation;

namespace WindowsIsland.PluginWorker;

internal sealed class WorkerWeChatHost(IsolationRpc rpc, CancellationToken lifetime) : IWeChatHost
{
    public Task<WeChatHostStatus> StartAsync() => CallAsync("start");
    public Task<WeChatHostStatus> PollAsync() => CallAsync("poll");
    public Task<WeChatHostStatus> RetryAsync() => CallAsync("retry");

    private async Task<WeChatHostStatus> CallAsync(string operation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        return await rpc.CallAsync<WeChatHostStatus>("wechat", operation, timeout.Token).ConfigureAwait(false)
            ?? throw new IOException("WeChat host service is unavailable.");
    }
}
