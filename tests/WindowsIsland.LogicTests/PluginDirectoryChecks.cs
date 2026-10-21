using System.Runtime.CompilerServices;
using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.LogicTests;

internal static class PluginDirectoryChecks
{
    public static readonly PluginManifest Manifest = new("wini_fixture_plugin", "外部电源来源", "1.0.0",
        "WindowsIsland.PluginFixture.dll", "WindowsIsland.PluginFixture.PowerSource", 1, ["notifications"], DeveloperId: "example");

    public static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }

    public static string Fixture => Path.Combine(AppContext.BaseDirectory, "fixture", Manifest.EntryAssembly);
    public static void WritePlugin(string directory, PluginManifest manifest)
    {
        Directory.CreateDirectory(directory);
        File.Copy(Fixture, Path.Combine(directory, Manifest.EntryAssembly), true);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, PluginManifestReader.Json));
    }

    public static async Task RunAsync()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "plugin-directories", Guid.NewGuid().ToString("N"));
        var paths = new PluginPaths(root);
        var store = new SettingsStore(Path.Combine(root, "settings.json"));
        Diagnostics.Initialize(Path.Combine(root, "diagnostics"), true);
        var code = paths.Code(PluginSourceKind.Manual, Manifest.Id);
        WritePlugin(code, Manifest);
        var broken = paths.Code(PluginSourceKind.Manual, "wini_broken_plugin");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "manifest.json"), "{broken");
        WritePlugin(paths.Code(PluginSourceKind.Manual, "wini_version_plugin"), Manifest with { Id = "wini_version_plugin", ContractVersion = 2 });
        WritePlugin(paths.Code(PluginSourceKind.Manual, "wini_entry_plugin"), Manifest with { Id = "wini_entry_plugin", EntryType = "Missing.Type" });
        var loader = new PluginDirectoryLoader(paths, store);
        Check(loader.Scan().Count == 2, "Directory discovery isolates corrupt manifests and incompatible contracts");
        using var manager = new PluginManager([], store);
        manager.ConfigureDirectories(paths);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Received += _ => arrived.TrySetResult();
        await manager.StartAsync();
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(manager.Snapshot().Single(s => s.Definition.Id == Manifest.Id).StatusKey == "Connected"
            && manager.Snapshot().Single(s => s.Definition.Id == "wini_entry_plugin").CanRetry,
            "Collectible external source publishes through the existing manager; missing entry fails alone");
        var failures = Diagnostics.Store!.Snapshot();
        Check(new[] { "wini_broken_plugin", "wini_version_plugin", "wini_entry_plugin" }.All(id => failures.Any(f => f.PluginId == id)),
            "Corrupt manifests, incompatible contracts and missing entries each produce isolated diagnostic records");
        var external = manager.Snapshot().Single(s => s.Definition.Id == Manifest.Id).Definition.Metadata!.External!;
        var data = paths.Data(Manifest.Id);
        File.WriteAllText(Path.Combine(data, "saved.txt"), "保留数据");
        await manager.RemoveAsync(Manifest.Id);
        Check(!Directory.Exists(code) && File.Exists(Path.Combine(data, "saved.txt")), "External removal deletes code and preserves plugin data");
        await external.VerifyUnloadAsync();
        Check(external.LastUnload is { IsAlive: false }, "External AssemblyLoadContext is reclaimed after Stop and Dispose");
        Check(loader.Scan().All(r => r.Definition.Id != Manifest.Id), "Deleted directory is absent on the next scan");
        foreach (var path in new[] { "../escape.dll", "/absolute.dll", "C:/absolute.dll", "inner/../escape.dll", "inner\\escape.dll" })
        {
            var rejected = false;
            try { PluginPaths.Relative(root, path); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Unsafe plugin path is rejected: " + path);
        }
        var old = JsonSerializer.Deserialize<PluginManifest>("""
            {"id":"wini_legacy_plugin","name":"旧清单","version":"1.0.0","entryAssembly":"a.dll","entryType":"A","contractVersion":1,"permissions":[]}
            """, PluginManifestReader.Json)!;
        Check(old.DeveloperId == "" && old.SourceKind == PluginSourceKind.Manual && old.DeveloperName is null,
            "Old manifests retain defaults for new developer and source metadata");
        await manager.ShutdownAsync();
        Diagnostics.Shutdown();
    }
}
