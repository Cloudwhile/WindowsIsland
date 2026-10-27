using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.PluginIsolation;

namespace WindowsIsland.Plugin.Cli;

internal sealed class TestHost(PluginManifest manifest, PluginTest.TestScenario scenario)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, object?> _settings = scenario.Settings?.ToDictionary(pair => pair.Key,
        pair => pair.Value.ValueKind == JsonValueKind.String ? (object?)pair.Value.GetString() : pair.Value.GetBoolean()) ?? [];
    private int _notifications, _networkRequests, _deniedNetworkRequests, _diagnostics, _pinnedRevision, _rejectedCallbacks;
    private sealed class ExpectedNetworkDenial : UnauthorizedAccessException { }
    private PinnedContent? _pinned;
    public int Notifications { get { lock (_gate) return _notifications; } }
    public int NetworkRequests { get { lock (_gate) return _networkRequests; } }
    public int DeniedNetworkRequests { get { lock (_gate) return _deniedNetworkRequests; } }
    public int Diagnostics { get { lock (_gate) return _diagnostics; } }
    public int PinnedRevision { get { lock (_gate) return _pinnedRevision; } }
    public int RejectedCallbacks { get { lock (_gate) return _rejectedCallbacks; } }

    public Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            try
            {
            object? result = null;
            switch (method)
            {
                case "getString":
                case "getBoolean":
                case "setString":
                case "setBoolean":
                    var setting = Read<WorkerSetting>(payload);
                    if (string.IsNullOrWhiteSpace(setting.Key) || setting.Key.Length > 128 || setting.Value?.Length > 32768)
                        throw new InvalidDataException();
                    if (method.StartsWith("get", StringComparison.Ordinal))
                        result = _settings.TryGetValue(setting.Key, out var value) ? value
                            : method == "getString" ? setting.Value : setting.Boolean;
                    else _settings[setting.Key] = method == "setString" ? setting.Value : setting.Boolean;
                    break;
                case "notification":
                    Require("notifications");
                    var notification = Read<PluginNotification>(payload);
                    if (notification.SourceId != manifest.Id) throw new InvalidDataException();
                    _notifications++;
                    break;
                case "pinned":
                    Require("notifications");
                    var change = Read<WorkerPinnedChange>(payload);
                    if (change.Kind == "Removed")
                    {
                        if (_pinned?.Id == change.Id) _pinned = null;
                    }
                    else if (change.Kind is "Added" or "Updated" && change.Content is { } content
                        && content.PluginId == manifest.Id && content.Id == change.Id && (content.Controls?.Count ?? 0) <= 32)
                    {
                        if (change.Kind == "Added" || _pinned?.Id == change.Id) _pinned = content;
                    }
                    else throw new InvalidDataException();
                    _pinnedRevision++;
                    break;
                case "network":
                    Require("network");
                    _ = Read<PluginNetworkRequest>(payload);
                    _networkRequests++;
                    // 独立功能测试仅提供固定模拟响应，不创建 HTTP 客户端。
                    if (!scenario.StubNetwork) { _deniedNetworkRequests++; throw new ExpectedNetworkDenial(); }
                    result = new PluginNetworkResponse(200, new Dictionary<string, string>(), []);
                    break;
                case "diagnostic":
                    _ = Read<PluginDiagnostic>(payload);
                    _diagnostics++;
                    break;
                default: throw new InvalidDataException("未知测试宿主操作。");
            }
            return Task.FromResult(result);
            }
            catch (ExpectedNetworkDenial) { throw; }
            catch { _rejectedCallbacks++; throw; }
        }
    }

    public void ValidateInteraction(PinnedControlInteraction interaction)
    {
        lock (_gate)
        {
            var control = _pinned?.Controls?.SingleOrDefault(c => c.Id == interaction.ControlId);
            if (_pinned?.Id != interaction.ContentId || control is not { IsEnabled: true })
                throw new InvalidDataException("plugin.test.json $.pinnedInteractions: 控件不存在或尚未可用。");
            if (control.Kind == PinnedControlKind.Toggle && interaction.IsOn is null
                || control.Kind == PinnedControlKind.Slider && (interaction.Value is not { } value
                    || !double.IsFinite(value) || value < control.Minimum || value > control.Maximum))
                throw new InvalidDataException("plugin.test.json $.pinnedInteractions: 控件值无效。");
        }
    }

    public async Task WaitForUpdate(int previousRevision, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        while (PinnedRevision <= previousRevision) await Task.Delay(25, timeout.Token);
    }

    public void CheckExpectations()
    {
        lock (_gate)
        {
            if (_notifications < scenario.MinimumNotifications || _notifications > scenario.MaximumNotifications)
                throw new InvalidOperationException($"通知数量 {_notifications} 不符合场景预期。");
            if (_networkRequests != scenario.NetworkRequests)
                throw new InvalidOperationException($"模拟网络请求数量 {_networkRequests} 不符合场景预期。");
            if (_deniedNetworkRequests != scenario.DeniedNetworkRequests)
                throw new InvalidOperationException($"模拟网络拒绝数量 {_deniedNetworkRequests} 不符合场景预期。");
            if (scenario.ExpectedCompactText is { } expected && _pinned?.CompactText != expected)
                throw new InvalidOperationException("固定内容交互结果不符合场景预期。");
            CheckDiagnostics();
        }
    }

    public void CheckDiagnostics()
    {
        lock (_gate)
        {
            if (_rejectedCallbacks != 0)
                throw new InvalidOperationException($"测试宿主拒绝了 {_rejectedCallbacks} 个非法回调。");
            if (_diagnostics != scenario.Diagnostics)
                throw new InvalidOperationException($"插件诊断数量 {_diagnostics} 不符合场景预期 {scenario.Diagnostics}。");
        }
    }

    private void Require(string permission)
    {
        if (!manifest.Permissions.Contains(permission)) throw new UnauthorizedAccessException();
    }

    private static T Read<T>(JsonElement payload) => payload.Deserialize<T>(IsolationRpc.Json)
        ?? throw new InvalidDataException();
}
