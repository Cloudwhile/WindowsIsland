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
        var received = new TaskCompletionSource<IslandNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Received += notification => received.TrySetResult(notification);
        var manifest = Manifest with { Permissions = ["notifications", "settings", "process", "network", "unknown"] };
        using (var forged = await installer.PrepareAsync(Zip(root, manifest with { SourceKind = PluginSourceKind.Official, DeveloperId = PluginCatalogClient.OfficialDeveloperId })))
            Check(forged.Manifest.SourceKind == PluginSourceKind.Manual && !forged.Trust.IsGranted("process"),
                "A manually imported package cannot self-declare trusted official provenance");
        using (var prepared = await installer.PrepareAsync(Zip(root, manifest)))
        {
            Check(prepared.Trust.Granted.SequenceEqual(["notifications", "settings"]) && prepared.Trust.Denied.SequenceEqual(["process", "network", "unknown"]),
                "Manual installation refuses all high risk and unknown permissions");
            await manager.InstallAsync(prepared);
        }
        var code = paths.Code(PluginSourceKind.Manual, manifest.Id);
        Check(Directory.Exists(code) && manager.Snapshot().Count == 1, "ZIP installs into the manual slot and starts without restarting the host");
        var restrictedNotification = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(restrictedNotification.OriginProcessId is null && !restrictedNotification.AllowActivation,
            "A manually installed plugin cannot assign a process activation target without a host grant");
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
            try { await manager.InstallAsync(prepared); } catch (InvalidDataException) { rejected = true; }
        }
        Check(rejected && PluginManifestReader.Read(code).Version == "2.0.0", "Downgrade rejection preserves the installed version");
        using (var prepared = await installer.PrepareAsync(Zip(root, manifest with { Version = "3.0.0", EntryType = "WindowsIsland.PluginFixture.BrokenSource" })))
        {
            Check(prepared.Manifest.Version == "3.0.0", "Metadata validation never executes a throwing plugin constructor");
            rejected = false;
            try { await manager.InstallAsync(prepared); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && PluginManifestReader.Read(code).Version == "2.0.0" && manager.Snapshot().Single().StatusKey == "Connected",
                "A plugin start failure rolls back files, permissions and the running previous instance");
        }
        foreach (var extra in new[] { "../escape", "/absolute", "C:/absolute", "folder/manifest.json", "CON", "x/../../escape" })
        {
            rejected = false;
            try { using var prepared = await installer.PrepareAsync(Zip(root, manifest, extra)); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "ZIP structure rejects " + extra);
        }
        foreach (var invalid in new[] { manifest with { Id = "invalid" }, manifest with { Id = BuiltInPluginIds.System }, manifest with { ContractVersion = 2 },
            manifest with { EntryType = "Missing.Type" }, manifest with { Version = "1.02.0" } })
        {
            rejected = false;
            try { using var prepared = await installer.PrepareAsync(Zip(root, invalid)); } catch (InvalidDataException) { rejected = true; }
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
        try { using var prepared = await installer.PrepareAsync(symlinkZip); } catch (InvalidDataException) { rejected = true; }
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
                && manager.Snapshot().Single().StatusKey == "Connected", "A locked code file restores plugin state, files and the running instance on removal failure");
        }
        var data = paths.Data(manifest.Id);
        var dataSettings = new SettingsStore(Path.Combine(data, "settings.json"));
        dataSettings.SetPluginString(manifest.Id, "message", "恢复的通知");
        await manager.RemoveAsync(manifest.Id);
        Check(!Directory.Exists(code) && !Directory.Exists(code + ".bak") && File.Exists(Path.Combine(data, "settings.json")),
            "Uninstall removes current code and backups while preserving plugin settings");
        var restored = new TaskCompletionSource<IslandNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Received += notification => { if (notification.Body == "恢复的通知") restored.TrySetResult(notification); };
        using (var prepared = await installer.PrepareAsync(Zip(root, manifest))) await manager.InstallAsync(prepared);
        await restored.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!store.IsPluginRemoved(manifest.Id), "Reinstalling the same plugin restores its previous data and clears the removal marker");
        await manager.ShutdownAsync();
    }
}
