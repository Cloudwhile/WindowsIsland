using System.Runtime.InteropServices;
using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;
using static WindowsIsland.LogicTests.PluginDirectoryChecks;

namespace WindowsIsland.LogicTests;

// 只验证元数据、授权和拒绝路径；真实 worker 的内核边界由独立安全夹具验证。
internal static class PluginConsentChecks
{
    private static PluginNetworkDisclosure Network => new(["https://example.com"], "发送插件自己的状态", ["插件状态"]);

    public static async Task RunAsync()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "plugin-consent", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        CheckMetadata();
        await CheckEnableAsync(Path.Combine(root, "enable"));
        await CheckNetworkAsync(Path.Combine(root, "network"));
        CheckMigration(Path.Combine(root, "migration"));
        CheckLinks(Path.Combine(root, "links"));
    }

    private static void CheckMetadata()
    {
        var valid = Manifest with { Permissions = ["notifications", "settings", "network"],
            Terms = new("修订一", "请在启用前阅读此包内纯文本条款。"), Network = Network };
        PluginManifestReader.Validate(valid);
        var restored = JsonSerializer.Deserialize<PluginManifest>(JsonSerializer.Serialize(valid, PluginManifestReader.Json), PluginManifestReader.Json)!;
        Check(restored.Terms == valid.Terms && restored.Network!.Origins.SequenceEqual(valid.Network!.Origins)
            && restored.Network.DataCategories.SequenceEqual(valid.Network.DataCategories),
            "Package terms and network disclosures round-trip as plain contract data");
        foreach (var invalid in new[]
        {
            valid with { Terms = new("", "正文") }, valid with { Terms = new("r1", " ") },
            valid with { Terms = new("r1", new string('中', 22000)) },
            valid with { Permissions = ["settings"] },
            valid with { Network = Network with { Origins = ["http://example.com"] } },
            valid with { Network = Network with { Origins = ["https://example.com:8443"] } },
            valid with { Network = Network with { Origins = ["https://example.com/path"] } },
            valid with { Network = Network with { Origins = ["https://user@example.com"] } },
            valid with { Network = Network with { Origins = ["https://example.com", "https://example.com:443/"] } },
            valid with { Network = Network with { Origins = [] } },
            valid with { Network = Network with { Purpose = new string('p', 1001) } },
            valid with { Network = Network with { DataCategories = [new string('d', 129)] } }
        })
            Rejected(() => PluginManifestReader.Validate(invalid), "Invalid terms, origins or network disclosure are rejected before execution");
        foreach (var kind in Enum.GetValues<PluginSourceKind>())
        {
            var trust = PluginTrust.Create(valid with { SourceKind = kind, DeveloperId = "winiofficial",
                Permissions = ["notifications", "settings", "network", "process", "native", "automation", "filesystem", "unknown"] },
                ["process", "native", "automation", "filesystem", "network"]);
            Check(trust.Granted.SequenceEqual(["notifications", "settings", "network"])
                && trust.Denied.Length == 5, "Neither official identity nor confirmation grants process, native, automation or broad filesystem access: " + kind);
        }
    }

    private static async Task CheckEnableAsync(string root)
    {
        var paths = new PluginPaths(root);
        var store = new SettingsStore(Path.Combine(root, "settings.json"));
        var manifest = Manifest with { EntryType = "WindowsIsland.PluginFixture.BrokenSource", Permissions = ["notifications", "settings"],
            Terms = new("r1", "插件包内的完整条款。") };
        var code = paths.Code(PluginSourceKind.Manual, manifest.Id);
        WritePlugin(code, manifest);
        var external = new PluginDirectoryLoader(paths, store).Scan().Single().Definition.Metadata!.External!;
        Directory.CreateDirectory(external.DataDirectory);
        File.WriteAllText(Path.Combine(external.DataDirectory, "host-permissions.json"),
            "{\"version\":\"1.0.0\",\"developerId\":\"example\",\"granted\":[\"process\",\"network\"]}");
        new SettingsStore(Path.Combine(external.DataDirectory, "settings.json")).SetPluginString(manifest.Id, "secret", "legacy-untrusted");
        Check(!PluginConsentPolicy.HasConsent(external), "Legacy plugin-writable permission receipts do not authorize execution");
        Rejected(() => external.Create(), "External Create enforces consent before constructing even a proxy");
        var hostPath = PluginConsentPolicy.HostSettingsPath(external);
        Check(!hostPath.StartsWith(external.DataDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && new SettingsStore(hostPath).GetPluginString(manifest.Id, "secret", null) is null,
            "External preferences use a host-owned location and never use the legacy file as a live backing store");

        store.SetPluginBoolean(manifest.Id, "Host.Enabled", true);
        using (var legacy = new PluginManager([], store))
        {
            legacy.ConfigureDirectories(paths);
            await legacy.StartAsync();
            Check(legacy.Snapshot().Single().StatusKey == "Off" && !store.GetPluginBoolean(manifest.Id, "Host.Enabled", true)
                && await legacy.GetSettingsAsync(manifest.Id) is null,
                "Startup disables a legacy enabled preference without running plugin constructors, schema or cleanup");
            await legacy.ShutdownAsync();
        }

        using var manager = new PluginManager([], store);
        manager.ConfigureDirectories(paths);
        await manager.RescanAsync(); // 管理器尚未 Start；确认测试不会启动 worker。
        await manager.SetEnabledAsync(manifest.Id, true);
        Check(!store.GetPluginBoolean(manifest.Id, "Host.Enabled", true), "Missing confirmation UI leaves external plugins disabled");
        var confirmations = 0;
        manager.ConfirmEnableAsync = descriptor =>
        {
            confirmations++;
            Check(descriptor.Terms == manifest.Terms && descriptor.PluginId == manifest.Id
                && descriptor.PackageSha256.Length == 64 && descriptor.DisclosureSha256.Length == 64,
                "Enable confirmation receives complete terms and the exact package/disclosure hashes");
            return Task.FromResult(false);
        };
        await manager.SetEnabledAsync(manifest.Id, true);
        Check(confirmations == 1 && !PluginConsentPolicy.HasConsent(external), "Declining terms persists no consent receipt");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.ConfirmEnableAsync = _ => { confirmations++; entered.SetResult(); return answer.Task; };
        var enabling = manager.SetEnabledAsync(manifest.Id, true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await manager.SetEnabledAsync(manifest.Id, false).WaitAsync(TimeSpan.FromSeconds(5));
        answer.SetResult(true);
        await enabling.WaitAsync(TimeSpan.FromSeconds(5));
        Check(confirmations == 2 && !store.GetPluginBoolean(manifest.Id, "Host.Enabled", true)
            && !PluginConsentPolicy.HasConsent(external),
            "Disabling while consent is pending finishes immediately and rejects a late affirmative answer");
        manager.ConfirmEnableAsync = _ =>
        {
            confirmations++;
            File.WriteAllText(Path.Combine(code, "changed-during-confirm.txt"), "new bytes");
            return Task.FromResult(true);
        };
        await RejectedAsync(() => manager.SetEnabledAsync(manifest.Id, true), "A package changed during confirmation cannot be enabled by the old dialog");
        Check(!PluginConsentPolicy.HasConsent(external) && !store.GetPluginBoolean(manifest.Id, "Host.Enabled", true),
            "A stale package confirmation leaves both execution preference and consent closed");
        manager.ConfirmEnableAsync = _ => { confirmations++; return Task.FromResult(true); };
        await manager.SetEnabledAsync(manifest.Id, true);
        Check(confirmations == 4 && PluginConsentPolicy.HasConsent(external)
            && store.GetPluginBoolean(manifest.Id, "Host.Enabled", false)
            && await manager.GetSettingsAsync(manifest.Id) is null,
            "Explicit agreement records the bound consent, while an unstarted manager still executes no schema");
        await manager.SetEnabledAsync(manifest.Id, false);
        await manager.SetEnabledAsync(manifest.Id, true);
        Check(confirmations == 4, "Unchanged package terms reuse the recorded agreement on a later enable");
        await manager.SetEnabledAsync(manifest.Id, false);
        var approved = PluginConsentPolicy.Describe(external);
        File.WriteAllText(Path.Combine(code, "changed.txt"), "changed package bytes");
        Check(!PluginConsentPolicy.HasConsent(external), "Changing actual package contents invalidates the previous consent");
        Rejected(() => PluginConsentPolicy.Grant(external, approved), "A stale confirmation cannot authorize a modified package");
        Rejected(() => external.Create(), "Modified package contents cannot bypass the Create gate");
        PluginConsentPolicy.Grant(external, PluginConsentPolicy.Describe(external));
        var changedTerms = manifest with { Terms = new("r2", "已更改的条款。") };
        File.WriteAllText(Path.Combine(code, "manifest.json"), JsonSerializer.Serialize(changedTerms, PluginManifestReader.Json));
        Check(!PluginConsentPolicy.HasConsent(external), "Changed terms invalidate consent even when plugin version is unchanged");
        await manager.RemoveAsync(manifest.Id);
        Check(!Directory.Exists(code) && File.Exists(Path.Combine(external.DataDirectory, "settings.json"))
            && !File.Exists(Path.Combine(paths.Root, "host-consents", manifest.Id + ".json")),
            "Removing an unstarted external plugin runs no hooks, retains its data and revokes its host-owned consent");
    }

    private static async Task CheckNetworkAsync(string root)
    {
        var paths = new PluginPaths(root);
        var manifest = Manifest with { Permissions = ["network"], Network = Network };
        var code = paths.Code(PluginSourceKind.Manual, manifest.Id);
        WritePlugin(code, manifest);
        var external = new ExternalPlugin(paths, manifest, code);
        var missing = manifest with { Network = null };
        WritePlugin(code, missing);
        var undisclosed = new ExternalPlugin(paths, missing, code);
        Rejected(() => PluginConsentPolicy.Describe(undisclosed), "Missing developer and default-source notice cannot authorize a network plugin");
        WritePlugin(code, manifest);
        PluginConsentPolicy.Grant(external, PluginConsentPolicy.Describe(external));
        var request = new PluginNetworkRequest("https://example.com/upload", "POST", Network.Purpose, Body: [1, 2, 3]);
        await RejectedAsync(() => new PluginNetworkBroker(external, () => false).SendAsync(request),
            "Revoked plugin sessions cannot use the host network service");
        foreach (var invalid in new[] { request with { Url = "http://example.com/upload" },
            request with { Url = "https://other.example/upload" }, request with { Purpose = "undeclared" },
            request with { Body = new byte[PluginNetworkLimits.MaximumRequestBytes + 1] },
            request with { Headers = new Dictionary<string, string> { ["Host"] = "other.example" } } })
            await RejectedAsync(() => new PluginNetworkBroker(external, () => true).SendAsync(invalid),
                "Invalid targets, undisclosed purposes, oversized bodies and forged transport headers are rejected before upload");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await RejectedAsync(() => new PluginNetworkBroker(external, () => true).SendAsync(request, canceled.Token),
            "Canceled network requests perform no upload");
        PluginConsentPolicy.Revoke(external);
        await RejectedAsync(() => new PluginNetworkBroker(external, () => true).SendAsync(request),
            "A missing host consent receipt prevents all network requests");
    }

    private static void CheckLinks(string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(root);
        var original = Path.Combine(root, "original.txt");
        var link = Path.Combine(root, "linked.txt");
        File.WriteAllText(original, "private canary");
        Check(CreateHardLink(link, original, 0), "The private NTFS hard-link fixture is created without touching other directories");
        Rejected(() => PluginFileSafety.CheckSingleLink(link), "Handle-level safety rejects hard links before granting access or reading host records");
        Check(File.ReadAllText(original) == "private canary", "Rejecting a private hard link leaves the original canary unchanged");
        File.Delete(link);
        PluginFileSafety.CheckSingleLink(original);
    }

    private static void CheckMigration(string root)
    {
        var paths = new PluginPaths(root);
        var external = new ExternalPlugin(paths, Manifest, paths.Code(PluginSourceKind.Manual, Manifest.Id));
        Directory.CreateDirectory(external.DataDirectory);
        var source = Path.Combine(external.DataDirectory, "settings.json");
        var legacy = new SettingsStore(source);
        legacy.SetPluginString(Manifest.Id, "message", "旧的有效设置");
        legacy.SetPluginBoolean(Manifest.Id, "enabled", false);
        legacy.SetPluginBoolean(Manifest.Id, "Host.Enabled", true);
        legacy.SetPluginString("wini_plugin_peer", "private", "peer canary");
        PluginSettingsMigration.EnsureMigrated(external);
        var target = PluginConsentPolicy.HostSettingsPath(external);
        var migrated = new SettingsStore(target);
        Check(migrated.GetPluginString(Manifest.Id, "message", null) == "旧的有效设置"
            && !migrated.GetPluginBoolean(Manifest.Id, "enabled", true), "Valid own-plugin legacy preferences survive one bounded snapshot migration");
        Check(!migrated.GetPluginBoolean(Manifest.Id, "Host.Enabled", false)
            && migrated.GetPluginString("wini_plugin_peer", "private", null) is null && !PluginConsentPolicy.HasConsent(external),
            "Migration imports neither host authorization nor another plugin's preferences");
        legacy.SetPluginString(Manifest.Id, "message", "a later plugin-writable change");
        PluginSettingsMigration.EnsureMigrated(external);
        Check(new SettingsStore(target).GetPluginString(Manifest.Id, "message", null) == "旧的有效设置",
            "Later edits to the old plugin-writable file cannot overwrite host-owned preferences");
        var invalid = new ExternalPlugin(paths, Manifest with { Id = "wini_plugin_corrupt_migration" }, "unused-code");
        Directory.CreateDirectory(invalid.DataDirectory);
        File.WriteAllText(Path.Combine(invalid.DataDirectory, "settings.json"), "invalid JSON");
        PluginSettingsMigration.EnsureMigrated(invalid);
        Check(File.Exists(PluginConsentPolicy.HostSettingsPath(invalid)), "A damaged legacy file produces an empty safe snapshot instead of blocking plugin startup");
        if (OperatingSystem.IsWindows())
        {
            var linked = new ExternalPlugin(paths, Manifest with { Id = "wini_plugin_linked_migration" }, "unused-code");
            Directory.CreateDirectory(linked.DataDirectory);
            Check(CreateHardLink(Path.Combine(linked.DataDirectory, "settings.json"), source, 0), "A private hard-link migration canary is prepared");
            PluginSettingsMigration.EnsureMigrated(linked);
            Check(!File.ReadAllText(PluginConsentPolicy.HostSettingsPath(linked)).Contains("later plugin-writable", StringComparison.Ordinal),
                "Legacy hard links are rejected without importing their target content");
        }
    }

    private static void Rejected(Action action, string message)
    {
        var rejected = false;
        try { action(); }
        catch (Exception error) when (error is PluginException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException) { rejected = true; }
        Check(rejected, message);
    }
    private static async Task RejectedAsync(Func<Task> action, string message)
    {
        var rejected = false;
        try { await action(); }
        catch (Exception error) when (error is PluginException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or OperationCanceledException) { rejected = true; }
        Check(rejected, message);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string name, string existing, nint security);
}
