using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsIsland.Contracts;
using WindowsIsland.PluginIsolation;
using WindowsIsland.Plugins;

namespace WindowsIsland.Plugin.Cli;

// 开发者显式执行的功能测试。只使用独立数据和模拟网络，不连接正式宿主。
internal static class PluginTest
{
    private static readonly JsonSerializerOptions Json = new(PluginManifestReader.Json)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true };

    public static async Task Run(string root)
    {
        var configPath = PluginPaths.Relative(root, "plugin.test.json");
        using var input = PluginFileSafety.OpenSingleLinkRead(configPath);
        if (input.Length > 64 * 1024) throw new InvalidDataException("plugin.test.json: 最大 64 KiB。");
        var plan = JsonSerializer.Deserialize<TestPlan>(input, Json);
        if (plan is not { SchemaVersion: 1, Scenarios.Length: > 0 and <= 16 })
            throw new InvalidDataException("plugin.test.json $.scenarios: 需要 1–16 个场景，schemaVersion 为 1。");
        foreach (var scenario in plan.Scenarios) scenario.Validate();
        var stage = await PackageProject.Build(root);
        var manifest = PackageProject.ValidateSource(stage);
        var testRoot = PluginPaths.Relative(root, ".wini/tests/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        SandboxAccess.ProtectHostDirectory(testRoot);
        var results = new List<object>();
        var allExited = true;
        try
        {
            for (var index = 0; index < plan.Scenarios.Length; index++)
            {
                var scenario = plan.Scenarios[index];
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var host = new TestHost(manifest, scenario);
                using var process = new SandboxProcess(new(manifest, testRoot, stage,
                    Path.Combine(testRoot, "data", index.ToString())));
                using var rpc = new IsolationRpc(process.Input, process.Output, host.HandleAsync);
                try
                {
                    await process.VerifyReadyAsync(rpc, timeout.Token);
                    var granted = manifest.Permissions.Where(p => p is "notifications" or "settings" or "network").ToArray();
                    await rpc.CallAsync<WorkerDescription>("initialize", new WorkerInitialization(manifest,
                        Path.Combine(testRoot, "data", index.ToString()), granted, null,
                        HostVersion: typeof(IIslandPlugin).Assembly.GetName().Version!.ToString(), CodeDirectory: stage), timeout.Token);
                    await rpc.CallAsync<WorkerDescription>("start", token: timeout.Token);
                    await Task.Delay(scenario.WaitMilliseconds, timeout.Token);
                    await host.WaitForActivity(timeout.Token);
                    foreach (var interaction in scenario.Interactions)
                    {
                        host.ValidateInteraction(interaction);
                        var revision = host.PinnedRevision;
                        await rpc.CallAsync<WorkerDescription>("interaction", interaction, timeout.Token);
                        await host.WaitForUpdate(revision, timeout.Token);
                    }
                    await rpc.CallAsync<WorkerDescription>("flush", token: timeout.Token);
                    host.CheckExpectations();
                    await rpc.CallAsync<WorkerDescription>("stop", token: timeout.Token);
                    var notifications = host.Notifications;
                    await Task.Delay(scenario.SettleMilliseconds, timeout.Token);
                    if (host.Notifications != notifications)
                        throw new InvalidOperationException("停止完成后仍有通知提交。");
                    host.CheckDiagnostics();
                    process.Dispose();
                    await process.ExitTask.WaitAsync(TimeSpan.FromSeconds(10));
                    results.Add(new { Scenario = index + 1, Passed = true, host.Notifications,
                        host.NetworkRequests, host.DeniedNetworkRequests, host.Diagnostics, host.PinnedRevision, host.RejectedCallbacks });
                    Console.WriteLine($"PASS: scenario {index + 1}, notifications={host.Notifications}, network={host.NetworkRequests}, pinned={host.PinnedRevision}");
                }
                catch (Exception error)
                {
                    results.Add(new { Scenario = index + 1, Passed = false, ErrorType = error.GetType().FullName, error.HResult });
                    throw;
                }
                finally
                {
                    process.Dispose();
                    try { await process.ExitTask.WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch { allExited = false; throw; }
                }
            }
        }
        finally
        {
            var profileRemoved = false;
            try
            {
                if (!allExited) throw new IOException("测试进程尚未确认退出，保留容器配置以便排查。");
                await TestProfile.Remove(testRoot, manifest.Id);
                profileRemoved = true;
            }
            finally
            {
                File.WriteAllText(Path.Combine(testRoot, "cleanup.json"), JsonSerializer.Serialize(new { ProfileRemoved = profileRemoved, AllWorkersExited = allExited }, Json));
                File.WriteAllText(Path.Combine(testRoot, "results.json"), JsonSerializer.Serialize(results, Json));
                Console.WriteLine(Path.Combine(testRoot, "results.json"));
            }
        }
    }

    private sealed record TestPlan(int SchemaVersion, TestScenario[] Scenarios);

    internal sealed record TestScenario(int WaitMilliseconds = 1500, int SettleMilliseconds = 200,
        int MinimumNotifications = 0, int MaximumNotifications = 0, int NetworkRequests = 0,
        bool StubNetwork = false, Dictionary<string, JsonElement>? Settings = null,
        int DeniedNetworkRequests = 0, int Diagnostics = 0,
        PinnedControlInteraction[]? PinnedInteractions = null, string? ExpectedCompactText = null)
    {
        [JsonIgnore] public PinnedControlInteraction[] Interactions => PinnedInteractions ?? [];
        public void Validate()
        {
            if (WaitMilliseconds is < 0 or > 10000 || SettleMilliseconds is < 0 or > 5000
                || MinimumNotifications < 0 || MaximumNotifications < MinimumNotifications || MaximumNotifications > 100
                || NetworkRequests is < 0 or > 100 || DeniedNetworkRequests < 0 || DeniedNetworkRequests > NetworkRequests
                || Diagnostics is < 0 or > 100 || Settings?.Count > 64 || Interactions.Length > 16
                || ExpectedCompactText?.Length > 200)
                throw new InvalidDataException("plugin.test.json: 场景时间、数量或预期值超出范围。");
            foreach (var pair in Settings ?? [])
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128
                    || pair.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.True or JsonValueKind.False)
                    || pair.Value.ValueKind == JsonValueKind.String && pair.Value.GetString()!.Length > 4096)
                    throw new InvalidDataException("plugin.test.json $.settings: 仅支持有界字符串和布尔值。");
        }
    }
}
