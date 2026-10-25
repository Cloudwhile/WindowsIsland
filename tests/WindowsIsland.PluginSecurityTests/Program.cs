using System.Diagnostics;
using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.PluginIsolation;
using WindowsIsland.Plugins;

namespace WindowsIsland.PluginSecurityTests;

internal static class Program
{
    private static int _checks;

    public static async Task<int> Main(string[] arguments)
    {
        if (!OperatingSystem.IsWindows() || arguments.Length != 4 || arguments[0] != "--worker" || arguments[2] != "--fixture")
        {
            Console.Error.WriteLine("Usage: WindowsIsland.PluginSecurityTests --worker <published-worker-directory> --fixture <security-fixture.dll>");
            return 2;
        }
        var root = Path.GetFullPath(Path.Combine("artifacts", "verification", "windowsisland-plugin-security-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await RunAsync(Path.GetFullPath(arguments[1]), Path.GetFullPath(arguments[3]), root, watchdog.Token);
            await LifecycleChecks.RunAsync(Path.GetFullPath(arguments[3]), root, watchdog.Token, Check);
            Check(await ProfileCleanup.RemoveAsync(root), "Only this fixture's fixed private AppContainer profiles are removed");
            var reportPath = Path.Combine(root, "security.json");
            var report = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(reportPath, watchdog.Token))!;
            report["Checks"] = _checks;
            report["LifecycleScenarios"] = JsonSerializer.SerializeToNode(new[] { "normal", "hangStart", "hangStopping", "hangStop", "suspended" });
            await File.WriteAllTextAsync(reportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), watchdog.Token);
            await File.WriteAllTextAsync(Path.Combine(root, "security.success"), _checks.ToString());
            Console.WriteLine($"{_checks} checks passed. Evidence: {root}");
            return 0;
        }
        catch (Exception error)
        {
            try { await ProfileCleanup.RemoveAsync(root); } catch { }
            await File.WriteAllTextAsync(Path.Combine(root, "security.error"), error.ToString());
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static async Task RunAsync(string worker, string fixture, string root, CancellationToken token)
    {
        Check(File.Exists(Path.Combine(worker, "WindowsIsland.PluginWorker.exe")), "Published worker exists");
        Check(Path.GetFileName(fixture) == "WindowsIsland.PluginSecurityFixture.dll" && File.Exists(fixture), "Controlled canary fixture exists");
        CopyTree(worker, Path.Combine(AppContext.BaseDirectory, "PluginWorker"));
        var ownCode = Path.Combine(root, "own-code");
        var peerCode = Path.Combine(root, "peer-code");
        Directory.CreateDirectory(ownCode); Directory.CreateDirectory(peerCode);
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(fixture)!).Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)))
        {
            File.Copy(file, Path.Combine(ownCode, Path.GetFileName(file)), true);
            File.Copy(file, Path.Combine(peerCode, Path.GetFileName(file)), true);
        }
        var hostPrivate = Path.Combine(root, "host-private");
        var ownData = Path.Combine(root, "own-data");
        var peerData = Path.Combine(root, "other-plugin");
        foreach (var directory in new[] { hostPrivate, ownData, peerData }) Directory.CreateDirectory(directory);
        var hostCanary = Path.Combine(hostPrivate, "host-canary.txt");
        var peerCanary = Path.Combine(peerData, "other-plugin-canary.txt");
        var receipt = Path.Combine(hostPrivate, "host-consent-canary.json");
        await File.WriteAllTextAsync(hostCanary, "fixed-host-private-canary", token);
        await File.WriteAllTextAsync(peerCanary, "fixed-peer-private-canary", token);
        await File.WriteAllTextAsync(receipt, "fixed-host-consent-canary", token);
        SandboxAccess.ProtectTree(hostPrivate);
        SandboxAccess.ProtectTree(peerData);
        using var listener = new LoopbackCanary();
        using var windowCanary = new HostWindowCanary();
        Check(windowCanary.OwnTitleAvailable(), "The host can read its own synthetic private window canary");
        var plan = JsonSerializer.Serialize(new { Root = root, HostProcessId = Environment.ProcessId, LoopbackPort = listener.Port, ProbeBroker = true,
            HostWindow = windowCanary.Handle.ToInt64() });
        await File.WriteAllTextAsync(Path.Combine(ownCode, "worker-canary.json"), plan, token);
        var marker = Path.Combine(ownData, "constructor.marker");
        Check(!File.Exists(marker), "No canary code executed before the launcher is invoked");

        var manifest = new PluginManifest("wini_plugin_security_fixture", "Security fixture", "1.0.0",
            Path.GetFileName(fixture), "WindowsIsland.PluginSecurityFixture.CanarySource", PluginContract.Version,
            [PluginPermissions.Notifications, PluginPermissions.Settings, PluginPermissions.Network]);
        var peerManifest = manifest with { Id = "wini_plugin_security_peer", EntryType = "WindowsIsland.PluginSecurityFixture.PeerSource" };
        var own = new ExternalPlugin(manifest, hostPrivate, ownCode, ownData);
        var peer = new ExternalPlugin(peerManifest, hostPrivate, peerCode, peerData);
        const string secretKey = "WINDOWSISLAND_SECURITY_FIXTURE_SECRET";
        var oldSecret = Environment.GetEnvironmentVariable(secretKey);
        Environment.SetEnvironmentVariable(secretKey, "fixed-parent-only-canary");
        SandboxProcess? ownProcess = null, peerProcess = null;
        int ownPid = 0, peerPid = 0;
        try
        {
            using var runningPeer = peerProcess = new SandboxProcess(peer);
            using var peerRpc = new IsolationRpc(runningPeer.Input, runningPeer.Output, new FixtureHost().HandleAsync);
            await runningPeer.VerifyReadyAsync(peerRpc, token);
            try { await peerRpc.CallAsync<WorkerDescription>("initialize", new WorkerInitialization(peerManifest, peerData, [], null, "fixture-host", CodeDirectory: peerCode), token); }
            catch { Console.Error.WriteLine("Peer native exit: " + (runningPeer.ExitCode?.ToString("X8") ?? "STILL_ACTIVE")); throw; }
            using var runningOwn = ownProcess = new SandboxProcess(own);
            ownPid = runningOwn.ProcessId; peerPid = runningPeer.ProcessId;
            Check(runningOwn.Sid != runningPeer.Sid, "Peer plugins have different verified AppContainer SIDs");
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "WindowsIsland.PluginSecurityFixture"),
                "Host did not load the external fixture assembly");
            var host = new FixtureHost();
            host.Settings["SecurityFixture.Plan"] = plan;
            using var rpc = new IsolationRpc(runningOwn.Input, runningOwn.Output, host.HandleAsync);
            var earlyRejected = false;
            try
            {
                await rpc.CallAsync<WorkerDescription>("initialize",
                    new WorkerInitialization(manifest, ownData, manifest.Permissions, null, CodeDirectory: ownCode), token);
            }
            catch (InvalidOperationException) { earlyRejected = true; }
            Check(earlyRejected && !File.Exists(marker), "Initialization before the verified ready handshake cannot execute plugin code");
            await runningOwn.VerifyReadyAsync(rpc, token);
            var initialized = await rpc.CallAsync<WorkerDescription>("initialize",
                new WorkerInitialization(manifest, ownData, manifest.Permissions, "0.9.0", "fixture-host", CodeDirectory: ownCode), token);
            Check(initialized?.Settings.Length == 2, "Remote initialization returns the plugin settings schema");
            Check(initialized!.Settings[1].DefaultValue is JsonElement { ValueKind: JsonValueKind.True }, "Boolean schema defaults survive JSON transport");
            Check(File.Exists(marker), "Constructor executed only inside the launched worker");
            var started = await rpc.CallAsync<WorkerDescription>("start", token: token);
            Check(started?.Status.State == PluginConnectionState.Connected, "Worker lifecycle status is returned after start");
            var result = host.Result();
            Check(Flag(result, "OwnReadWrite"), "Worker reads and writes its own private canary");
            foreach (var key in new[] { "HostReadable", "HostWritable", "OtherPluginReadable", "OtherPluginWritable", "ReceiptReadable", "ReceiptWritable",
                "NativeHostReadable", "HostProcessAccessible", "DirectHttpSucceeded", "DirectSocketSucceeded", "EnvironmentSecretPresent", "ChildProcessCreated", "HostWindowReadable", "OtherPluginCodeReadable",
                "HostAclWritable", "PeerAclWritable", "ReceiptAclWritable", "HostHandlesAccessible" })
                Check(!Flag(result, key), "Isolation denies " + key);
            Check(result.GetProperty("TokenSid").GetString() == runningOwn.Sid, "Fixture reports the same AppContainer SID independently verified by the launcher");
            Check(Flag(result, "SettingsRoundTrip"), "Synchronous settings calls round trip without pipe deadlock");
            Check(Flag(result, "BrokerAvailable") && !Flag(result, "BrokerSucceeded") && host.NetworkRequests == 1,
                "Cancellation of the in-memory broker request prevents the controlled upload");
            Check(host.LastNetworkUrl == "https://security-fixture.invalid/upload" && host.LastNetworkBody == "fixed-isolated-canary",
                "Only fixed fixture data and the fixed synthetic destination cross the network RPC");
            Check(listener.Connections == 0, "No direct HTTP or socket reached the real loopback canary");
            Check(await File.ReadAllTextAsync(hostCanary, token) == "fixed-host-private-canary"
                && await File.ReadAllTextAsync(peerCanary, token) == "fixed-peer-private-canary"
                && await File.ReadAllTextAsync(receipt, token) == "fixed-host-consent-canary", "Host, peer and receipt canaries remain byte-for-byte intact");
            await rpc.CallAsync<WorkerDescription>("poll", token: token);
            await rpc.CallAsync<WorkerDescription>("retry", token: token);
            await rpc.CallAsync<WorkerDescription>("interaction", new PinnedControlInteraction("fixture-pin", "fixture-action"), token);
            result = host.Result();
            Check(result.GetProperty("PollCount").GetInt32() == 1 && result.GetProperty("RetryCount").GetInt32() == 1
                && result.GetProperty("InteractionCount").GetInt32() == 1, "Poll, retry and pinned interaction reach only this worker");
            host.AllowNetwork = true;
            await rpc.CallAsync<WorkerDescription>("interaction", new PinnedControlInteraction("fixture-pin", "fixture-upload"), token);
            Check(Flag(host.Result(), "BrokerSucceeded") && host.NetworkRequests == 2,
                "An explicitly approved in-memory response completes without external networking");
            result = host.Result();
            await EventuallyAsync(() => host.Notifications == 2 && host.PinnedChanges == 1 && host.Diagnostics == 1, token);
            Check(host.Notifications == 2 && host.PinnedChanges == 1 && host.Diagnostics == 1, "Both notification channels, diagnostics and pinned snapshots cross the bounded callback channel");
            await rpc.CallAsync<WorkerDescription>("stopping", PluginStopReason.Removed, token);
            await rpc.CallAsync<WorkerDescription>("stop", token: token);
            await rpc.CallAsync<WorkerDescription>("uninstall", token: token);
            Check(!host.Settings.ContainsKey("revokedProbe"), "Revoked settings cannot be written by a late stopping callback");
            Check(File.Exists(Path.Combine(ownData, "uninstall.marker")), "Uninstall hook runs inside the isolated worker");
            await File.WriteAllTextAsync(Path.Combine(root, "security.json"), JsonSerializer.Serialize(new
            {
                ActualWindowsAppContainer = true, ActualLpac = true, GlobalPreferencesChanged = false,
                OwnSid = runningOwn.Sid, PeerSid = runningPeer.Sid, HostProcess = Environment.ProcessId,
                WorkerProcess = ownPid, PeerProcess = peerPid, Result = result,
                NetworkBrokerScope = "In-memory allow/cancel fixture only; no UI consent or real upload is claimed.",
                Checks = _checks
            }, new JsonSerializerOptions { WriteIndented = true }), token);
        }
        finally
        {
            ownProcess?.Dispose(); peerProcess?.Dispose();
            try { await Task.WhenAll(ownProcess?.ExitTask ?? Task.CompletedTask, peerProcess?.ExitTask ?? Task.CompletedTask); }
            finally { Environment.SetEnvironmentVariable(secretKey, oldSecret); }
        }
        await EventuallyAsync(() => !IsRunning(ownPid) && !IsRunning(peerPid), token);
        Check(!IsRunning(ownPid) && !IsRunning(peerPid), "Disposing the sandbox jobs reclaims both private worker processes");
    }

    private static bool Flag(JsonElement result, string name) => result.GetProperty(name).GetBoolean();
    private static bool IsRunning(int pid)
    {
        if (pid <= 0) return false;
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
    private static async Task EventuallyAsync(Func<bool> condition, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private static void CopyTree(string source, string target)
    {
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("FAIL " + message);
        _checks++;
        Console.WriteLine("PASS " + message);
    }
}
