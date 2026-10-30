using System.Text.Json;
using WindowsIsland.Contracts;

namespace WindowsIsland.PluginIsolation;

public static class DesktopRpc
{
    public static async Task<object?> DispatchAsync(IPluginDesktop service, WorkerDesktopRequest request, CancellationToken token)
    {
        T Value<T>() => request.Payload.Deserialize<T>(IsolationRpc.Json) ?? throw new InvalidDataException();
        switch (request.Operation)
        {
            case "bridgeOpen": return await service.OpenBridgeAsync(Value<DesktopBridge>(), token);
            case "bridgeRead": return await service.ReadBridgeAsync(Value<int>(), token);
            case "bridgeClose": return await service.CloseBridgeAsync(Value<int>(), token);
            case "clients": return await service.FindClientsAsync(token);
            case "tree":
                var window = Value<DesktopWindowRequest>();
                return await service.ReadTreeAsync(window.ProcessId, window.Window, token);
            case "module": return await service.ReadModuleAsync(Value<DesktopRead>(), token);
            case "memory": return await service.ReadMemoryAsync(Value<DesktopRead>(), token);
            case "flag": return await service.SetFlagAsync(Value<DesktopFlag>(), token);
            case "image": return await service.ReadImageAsync(Value<DesktopImage>(), token) ?? [];
            default: throw new InvalidDataException("Unknown desktop operation.");
        }
    }
}
