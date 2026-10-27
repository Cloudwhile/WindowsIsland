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
        var consentExternal = manager.Snapshot().Single().Definition.Metadata!.External!;
        PluginConsentPolicy.Grant(consentExternal, PluginConsentPolicy.Describe(consentExternal));
        var firstGeneration = PluginConsentPolicy.ReadGeneration(paths.Root, manifest.Id);
        using (var prepared = await installer.PrepareAsync(Zip(root, manifest)))
        {
            try { await manager.InstallAsync(prepared); } catch (PluginAlreadyInstalledException) { same = true; }
            Check(same, "Same-version installation requires explicit overwrite confirmation");
            await manager.InstallAsync(prepared, overwrite: true);
        }
        consentExternal = manager.Snapshot().Single().Definition.Metadata!.External!;
        Check(!PluginConsentPolicy.HasConsent(consentExternal)
            && firstGeneration != PluginConsentPolicy.ReadGeneration(paths.Root, manifest.Id),
            "Identical same-version overwrite invalidates prior consent using a new host installation generation");
        PluginConsentPolicy.Grant(consentExternal, PluginConsentPolicy.Describe(consentExternal));
        using (var prepared = await installer.PrepareAsync(Zip(root, manifest, "new-content.txt")))
            await manager.InstallAsync(prepared, overwrite: true);
        consentExternal = manager.Snapshot().Single().Definition.Metadata!.External!;
        Check(!PluginConsentPolicy.HasConsent(consentExternal), "Same-version changed contents require fresh consent");
        PluginConsentPolicy.Grant(consentExternal, PluginConsentPolicy.Describe(consentExternal));
        var rollbackGeneration = PluginConsentPolicy.ReadGeneration(paths.Root, manifest.Id);
        using (var prepared = await installer.PrepareAsync(Zip(root, manifest)))
        using (var broken = new PreparedPlugin(prepared.Directory, prepared.Manifest with { EntryAssembly = "../invalid.dll" }, prepared.Trust))
        {
            var rolledBack = false;
            try { await manager.InstallAsync(broken, overwrite: true); }
            catch (PluginException) { rolledBack = true; }
            Check(rolledBack && rollbackGeneration == PluginConsentPolicy.ReadGeneration(paths.Root, manifest.Id)
                && PluginConsentPolicy.HasConsent(consentExternal) && File.Exists(Path.Combine(code, "new-content.txt")),
                "Failure after directory switch restores old package, installation generation and consent");
        }
        using (var prepared = await installer.PrepareAsync(Zip(root, manifest with { Version = "2.0.0" }))) await manager.InstallAsync(prepared);
        Check(!PluginConsentPolicy.HasConsent(manager.Snapshot().Single().Definition.Metadata!.External!),
            "Version upgrade invalidates prior consent");
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
        foreach (var url in new[] { "https://github.com/owner/repo", "https://github.com/owner/repo/",
            "https://github.com/owner/repo.git", "https://github.com/owner/repo?x=1", "https://github.com/owner/repo%2Fother" })
        {
            var accepted = PluginCatalogPolicy.Repository(url);
            Check(PluginManifestReader.GitHubRepository(url) == accepted, "Manifest and catalog repository policy agree: " + url);
            var installedUrl = true;
            try { using var prepared = await installer.PrepareAsync(Zip(root, manifest with { RepositoryUrl = url })); }
            catch (PluginException) { installedUrl = false; }
            Check(installedUrl == accepted, "ZIP installer applies the same repository policy: " + url);
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
        await CheckProvenanceAsync(Path.Combine(root, "provenance"));
    }

    private static async Task CheckProvenanceAsync(string root)
    {
        var paths = new PluginPaths(Path.Combine(root, "Plugins"));
        var store = new SettingsStore(Path.Combine(root, "settings.json"));
        var manifest = Manifest with { SourceKind = PluginSourceKind.Official, DeveloperId = "winiofficial" };
        var code = paths.Code(PluginSourceKind.Official, manifest.Id);
        WritePlugin(code, manifest);
        var loader = new PluginDirectoryLoader(paths, store);
        Check(loader.Scan().Count == 0 && loader.Scan().Count == 0,
            "Copying a forged official directory without a host receipt never registers an official plugin, including rescans");
        using var manager = new PluginManager([], store);
        manager.ConfigureDirectories(paths);
        using (var prepared = await new PluginInstaller(paths).PrepareAsync(Zip(root, manifest), PluginSourceKind.Official))
            await manager.InstallAsync(prepared, overwrite: true);
        Check(loader.Scan().Count == 1, "Host-installed catalog package survives a verified rescan");
        File.WriteAllText(Path.Combine(code, "manifest.json"), JsonSerializer.Serialize(manifest with { Author = "forged" }, PluginManifestReader.Json));
        Check(loader.Scan().Count == 0, "Changing the manifest invalidates catalog provenance");
        WritePlugin(code, manifest);
        File.AppendAllText(Path.Combine(code, manifest.EntryAssembly), "tampered");
        Check(loader.Scan().Count == 0, "Changing package code invalidates catalog provenance");
        await manager.ShutdownAsync();
    }
}
