using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.PluginIsolation;

namespace WindowsIsland.PluginSecurityTests;

internal sealed class FixtureHost
{
    public ConcurrentDictionary<string, object> Settings { get; } = new(StringComparer.Ordinal);
    public bool AllowNetwork;
    public int NetworkRequests, Notifications, Diagnostics, PinnedChanges;
    public string? LastNetworkBody;
    public string? LastNetworkUrl;

    public Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        object? result;
        switch (method)
        {
            case "getString":
                var readString = Read<WorkerSetting>(payload);
                result = Settings.TryGetValue(readString.Key, out var text) && text is string ? text : readString.Value;
                break;
            case "getBoolean":
                var readBoolean = Read<WorkerSetting>(payload);
                result = Settings.TryGetValue(readBoolean.Key, out var flag) && flag is bool ? flag : readBoolean.Boolean;
                break;
            case "setString":
                var writeString = Read<WorkerSetting>(payload);
                if (writeString.Value is null) Settings.TryRemove(writeString.Key, out _);
                else Settings[writeString.Key] = writeString.Value;
                result = null;
                break;
            case "setBoolean":
                var writeBoolean = Read<WorkerSetting>(payload);
                Settings[writeBoolean.Key] = writeBoolean.Boolean;
                result = null;
                break;
            case "network":
                var request = Read<PluginNetworkRequest>(payload);
                Interlocked.Increment(ref NetworkRequests);
                LastNetworkBody = Encoding.UTF8.GetString(request.Body ?? []);
                LastNetworkUrl = request.Url;
                if (!AllowNetwork) throw new OperationCanceledException("Fixed canary user cancellation.");
                result = new PluginNetworkResponse(200, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("fixed-approved-response"));
                break;
            case "notification":
                Read<PluginNotification>(payload);
                Interlocked.Increment(ref Notifications); result = null;
                break;
            case "diagnostic":
                Read<PluginDiagnostic>(payload);
                Interlocked.Increment(ref Diagnostics); result = null;
                break;
            case "pinned":
                Read<WorkerPinnedChange>(payload);
                Interlocked.Increment(ref PinnedChanges); result = null;
                break;
            default:
                throw new InvalidDataException("Invalid fixture request.");
        }
        return Task.FromResult(result);
    }

    public JsonElement Result()
    {
        if (!Settings.TryGetValue("SecurityFixture.Result", out var value) || value is not string json)
            throw new InvalidDataException("Fixture result is missing.");
        return JsonSerializer.SerializeToElement(JsonSerializer.Deserialize<JsonElement>(json));
    }

    private static T Read<T>(JsonElement value) => value.Deserialize<T>(IsolationRpc.Json)
        ?? throw new InvalidDataException("Invalid fixture payload.");
}
