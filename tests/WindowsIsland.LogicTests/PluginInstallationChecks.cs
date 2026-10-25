using System.IO.Compression;
using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;
using static WindowsIsland.LogicTests.PluginDirectoryChecks;

namespace WindowsIsland.LogicTests;

internal static class PluginInstallationChecks
{
    private static void Check(bool value, string message) => PluginDirectoryChecks.Check(value, message);
    public static string Zip(string root, PluginManifest manifest, string? extra = null)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open()))
            writer.Write(JsonSerializer.Serialize(manifest, PluginManifestReader.Json));
        archive.CreateEntryFromFile(Fixture, Manifest.EntryAssembly);
        if (extra is not null) using (var writer = new StreamWriter(archive.CreateEntry(extra).Open())) writer.Write("malicious");
        return path;
    }

    public static async Task RunAsync()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "plugin-installation", Guid.NewGuid().ToString("N"));
        var paths = new PluginPaths(Path.Combine(root, "Plugins"));
        var installer = new PluginInstaller(paths);
        var store = new SettingsStore(Path.Combine(root, "settings.json"));
        using var manager = new PluginManager([], store);
        manager.ConfigureDirectories(paths);
        await manager.StartAsync();
        var received = 0;
        manager.Received += _ => received++;
        var manifest = Manifest with { Permissions = ["notifications", "settings", "process", "network", "unknown"],
            Network = new(["https://example.com"], "发送插件自己的状态", ["插件状态"]) };
        using (var forged = await installer.PrepareAsync(Zip(root, manifest with { SourceKind = PluginSourceKind.Official, DeveloperId = PluginCatalogClient.OfficialDeveloperId })))
            Check(forged.Manifest.SourceKind == PluginSourceKind.Manual && !forged.Trust.IsGranted("process"),
                "A manually imported package cannot self-declare trusted official provenance");
        using (var prepared = await installer.PrepareAsync(Zip(root, manifest)))
        {
            Check(prepared.Trust.Granted.SequenceEqual(["notifications", "settings", "network"]) && prepared.Trust.Denied.SequenceEqual(["process", "unknown"]),
                "Manual installation exposes only safe services and the declared network broker, never direct process access");
            await manager.InstallAsync(prepared);
        }
        var code = paths.Code(PluginSourceKind.Manual, manifest.Id);
        Check(Directory.Exists(code) && manager.Snapshot().Count == 1 && manager.Snapshot().Single().StatusKey == "Off" && received == 0,
            "ZIP installation places code in the manual slot but never starts or publishes before explicit consent");
        var same = false;
        using (var prepared = await installer.PrepareAsync(Zip(root, manifest)))
        {
            try { await manager.InstallAsync(prepared); } catch (PluginAlreadyInstalledException) { same = true; }
            Check(same, "Same-version installation requires explicit overwrite confirmation");
            await manager.InstallAsync(prepared, overwrite: true);
        }
        using (var prepared = await installer.PrepareAsync(Zip(root, manifest with { Version = "2.0.0" }))) await manager.InstallAsync(prepared);
        Check(PluginManifestReader.Read(code).Version == "2.0.0" && PluginManifestReader.Read(code + ".bak").Version == "1.0.0",
            "Manual update atomically replaces code while retaining the previous version backup");
        var rejected = false;
        using (var prepared = await installer.PrepareAsync(Zip(root, manifest)))
        {
            try { await manager.InstallAsync(prepared); } catch (Exception error) when (error is PluginException or InvalidDataException) { rejected = true; }
        }
        Check(rejected && PluginManifestReader.Read(code).Version == "2.0.0", "Downgrade rejection preserves the installed version");
        using (var prepared = await installer.PrepareAsync(Zip(root, manifest with { Version = "3.0.0", EntryType = "WindowsIsland.PluginFixture.BrokenSource" })))
        {
            Check(prepared.Manifest.Version == "3.0.0", "Metadata validation never executes a throwing plugin constructor");
            await manager.InstallAsync(prepared);
            Check(PluginManifestReader.Read(code).Version == "3.0.0" && manager.Snapshot().Single().StatusKey == "Off" && received == 0,
                "Installation does not execute even a throwing constructor; runtime isolation is verified separately after consent");
        }
        foreach (var extra in new[] { "../escape", "/absolute", "C:/absolute", "folder/manifest.json", "CON", "x/../../escape" })
        {
            rejected = false;
            try { using var prepared = await installer.PrepareAsync(Zip(root, manifest, extra)); } catch (Exception error) when (error is PluginException or InvalidDataException) { rejected = true; }
            Check(rejected, "ZIP structure rejects " + extra);
        }
        foreach (var invalid in new[] { manifest with { Id = "invalid" }, manifest with { Id = BuiltInPluginIds.System }, manifest with { ContractVersion = 2 },
            manifest with { EntryType = "Missing.Type" }, manifest with { Version = "1.02.0" } })
        {
            rejected = false;
            try { using var prepared = await installer.PrepareAsync(Zip(root, invalid)); } catch (Exception error) when (error is PluginException or InvalidDataException) { rejected = true; }
            Check(rejected, "Invalid manifest or entry is rejected before execution: " + invalid.Id + "/" + invalid.EntryType + "/" + invalid.Version);
        }
        var symlinkZip = Zip(root, manifest);
        using (var archive = ZipFile.Open(symlinkZip, ZipArchiveMode.Update))
        {
            var link = archive.CreateEntry("link");
            link.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var writer = new StreamWriter(link.Open());
            writer.Write("../outside");
        }
        rejected = false;
        try { using var prepared = await installer.PrepareAsync(symlinkZip); } catch (Exception error) when (error is PluginException or InvalidDataException) { rejected = true; }
        Check(rejected, "ZIP symbolic-link metadata is rejected before extraction");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            rejected = false;
            try { using var prepared = await installer.PrepareAsync(Zip(root, manifest), token: cancellation.Token); } catch (OperationCanceledException) { rejected = true; }
            Check(rejected, "Plugin extraction honors cancellation");
        }
        Check(!Directory.EnumerateDirectories(paths.Slot(PluginSourceKind.Manual)).Any(p => Path.GetFileName(p).StartsWith('.')),
            "Failed and canceled imports leave no partial staging directories");
        var lockedPath = Path.Combine(code, "locked.txt");
        await File.WriteAllTextAsync(lockedPath, "retain");
        using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            rejected = false;
            try { await manager.RemoveAsync(manifest.Id); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = true; }
            Check(rejected && !store.IsPluginRemoved(manifest.Id) && File.Exists(Path.Combine(code, manifest.EntryAssembly))
                && manager.Snapshot().Single().StatusKey == "Off", "A locked code file restores plugin files and its disabled state on removal failure");
        }
        var data = paths.Data(manifest.Id);
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "retained.txt"), "保留插件自有内容");
        var hostSettings = PluginConsentPolicy.HostSettingsPath(manager.Snapshot().Single().Definition.Metadata!.External!);
        var dataSettings = new SettingsStore(hostSettings);
        dataSettings.SetPluginString(manifest.Id, "message", "恢复的通知");
        await manager.RemoveAsync(manifest.Id);
        Check(!Directory.Exists(code) && !Directory.Exists(code + ".bak") && File.Exists(hostSettings)
            && File.Exists(Path.Combine(data, "retained.txt")),
            "Uninstall removes code and backups while retaining plugin-owned data and separate host-owned preferences");
        using (var prepared = await installer.PrepareAsync(Zip(root, manifest))) await manager.InstallAsync(prepared);
        Check(!store.IsPluginRemoved(manifest.Id) && manager.Snapshot().Single().StatusKey == "Off" && received == 0
            && new SettingsStore(hostSettings).GetPluginString(manifest.Id, "message", null) == "恢复的通知",
            "Reinstallation clears the removal marker, preserves host-owned preferences and still requires explicit enablement");
        await manager.ShutdownAsync();
    }
}
