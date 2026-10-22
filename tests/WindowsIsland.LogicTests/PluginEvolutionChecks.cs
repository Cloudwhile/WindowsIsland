using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;
using static WindowsIsland.LogicTests.PluginDirectoryChecks;

namespace WindowsIsland.LogicTests;

internal static class PluginEvolutionChecks
{
    private static void Check(bool value, string message) => PluginDirectoryChecks.Check(value, message);
    public static async Task RunAsync()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "plugin-evolution", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Diagnostics.Initialize(Path.Combine(root, "diagnostics"), true);
        try
        {
            CheckLimits();
            await CheckLiveSettings(root);
            await CheckDiscoveryAndLifecycle(root);
            await CheckConcurrency(root);
        }
        finally { Diagnostics.Shutdown(); }
    }

    private static void CheckLimits()
    {
        Check(!typeof(IPluginContext).GetProperty(nameof(IPluginContext.HostVersion))!.GetMethod!.IsAbstract
            && !typeof(IPluginContext).GetProperty(nameof(IPluginContext.PreviousVersion))!.GetMethod!.IsAbstract,
            "New context properties provide default implementations for existing implementers");
        PluginManifestReader.Validate(Manifest with { Description = new('d', 8000), Author = new('a', 200) });
        foreach (var invalid in new[] { Manifest with { Description = new('d', 8001) }, Manifest with { Author = new('a', 201) } })
        {
            var rejected = false;
            try { PluginManifestReader.Validate(invalid); } catch (Exception error) when (error is PluginException or InvalidDataException) { rejected = true; }
            Check(rejected, "Manifest description/author limits reject oversized values");
        }
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        var settings = new PluginSettings(store, Manifest.Id);
        foreach (var key in new[] { "", " ", new string('k', 129) })
            foreach (Action action in new Action[] { () => settings.GetString(key), () => settings.GetBoolean(key),
                () => settings.SetString(key, "value"), () => settings.SetBoolean(key, true) })
            {
                var rejected = false;
                try { action(); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "All string/bool setting operations reject empty or oversized keys");
            }
        Check(settings.GetString(new('k', 128), "default") == "default", "A 128-character setting key remains valid");
    }

    private static async Task CheckDiscoveryAndLifecycle(string root)
    {
        var paths = new PluginPaths(Path.Combine(root, "discovery"));
        var store = new SettingsStore(Path.Combine(root, "settings.json"));
        var manifest = Manifest with { EntryType = "WindowsIsland.PluginFixture.SchemaSource", Permissions = ["notifications", "settings"] };
        var code = paths.Code(PluginSourceKind.Manual, manifest.Id);
        var data = paths.Data(manifest.Id);
        var log = Path.Combine(data, "lifecycle.txt");
        using var manager = new PluginManager([], store);
        manager.ConfigureDirectories(paths);
        await manager.StartAsync();
        Check(manager.Snapshot().Count == 0, "Initial scan has no external entries");
        store.SetPluginBoolean(manifest.Id, "Host.Enabled", false);
        WritePlugin(code, manifest);
        WritePlugin(paths.Code(PluginSourceKind.Official, "wini_rejected_plugin"), Manifest with { Id = "wini_rejected_plugin", EntryType = "Missing.Type" });
        await manager.RescanAsync();
        Check(manager.Snapshot().Single().Definition.Id == manifest.Id, "Public rescan discovers a new valid directory and rejects invalid PE metadata independently");
        Check(Diagnostics.Store!.Snapshot().Any(e => e.PluginId == "wini_rejected_plugin"), "Invalid rescan entries produce diagnostics");
        var model = (await manager.GetSettingsAsync(manifest.Id))!;
        Check(model.Fields.Count == 2 && model.Fields[0].DefaultValue is "default message" && !File.Exists(log),
            "Disabled lifecycle-only plugin exposes schema without initialization or starting");
        model.Settings.SetString("message", "edited while disabled");
        model.Settings.SetBoolean("enabled", false);
        await manager.SetEnabledAsync(manifest.Id, true);
        var events = File.ReadAllLines(log);
        Check(events.Any(e => e == "Settings:edited while disabled:False"), "Schema edits while disabled reach the initialized plugin through IPluginSettings");
        Check(events.Any(e => e == "Initialize:null:" + ReleaseVersion.Current.Tag), "First initialization receives host version and null previous version");
        Check(ReferenceEquals(model.Settings, (await manager.GetSettingsAsync(manifest.Id))!.Settings), "Runtime and settings editor share the same store");
        model.Settings.SetString("message", "live change");
        await manager.SetEnabledAsync(manifest.Id, false);
        await manager.PollAsync();
        events = File.ReadAllLines(log);
        Check(HasSequence(events, "Stopping:Disabled", "Stop", "Dispose"), "Disabled reason precedes StopAsync and Dispose");
        var held = code + ".held";
        Directory.Move(code, held);
        await manager.RescanAsync();
        Check(manager.Snapshot().Count == 1, "Rescan is additive and retains entries whose directories disappear");
        Directory.Move(held, code);
        await manager.SetEnabledAsync(manifest.Id, true);
        model.Settings.SetBoolean("failUninstall", true);
        var installer = new PluginInstaller(paths);
        using (var prepared = await installer.PrepareAsync(PluginInstallationChecks.Zip(root, manifest with { Version = "2.0.0" })))
            await manager.InstallAsync(prepared);
        events = File.ReadAllLines(log);
        Check(HasSequence(events, "Stopping:Updating", "Stop", "Dispose"), "Update forwards Updating to the old lifecycle-only instance before stopping");
        Check(events.Any(e => e == "Initialize:1.0.0:" + ReleaseVersion.Current.Tag) && events.Any(e => e == "Settings:live change:False"),
            "Updated instance receives PreviousVersion and preserves editable settings");
        await manager.SetEnabledAsync(manifest.Id, false);
        await manager.RemoveAsync(manifest.Id);
        events = File.ReadAllLines(log);
        Check(HasSequence(events, "Stopping:Removed", "Stop", "Uninstall:True", "Dispose") && !Directory.Exists(code),
            "Disabled plugin is initialized only for cleanup; uninstall failure cannot block directory deletion");
        Check(Diagnostics.Store!.Snapshot().Any(e => e.PluginId == manifest.Id && e.Operation == "Uninstall"), "Uninstall failure produces a diagnostic");
        WritePlugin(code, manifest);
        await manager.RescanAsync();
        Check(manager.Snapshot().Count == 0 && store.IsPluginRemoved(manifest.Id), "Rescan cannot resurrect a deleted plugin even if its directory returns");
        using (var restarted = new PluginManager([], new SettingsStore(Path.Combine(root, "settings.json"))))
        {
            restarted.ConfigureDirectories(paths);
            await restarted.StartAsync();
            Check(restarted.Snapshot().Count == 0, "Startup discovery also respects persisted removal markers");
            await restarted.ShutdownAsync();
        }
        await manager.ShutdownAsync();
    }

    private static bool HasSequence(string[] values, params string[] sequence) => Enumerable.Range(0, Math.Max(0, values.Length - sequence.Length + 1))
        .Any(index => values.Skip(index).Take(sequence.Length).SequenceEqual(sequence));

    private static async Task CheckLiveSettings(string root)
    {
        var store = new SettingsStore(Path.Combine(root, "live-settings.json"));
        var source = new SchemaFixture();
        var definition = new NotificationPluginDefinition(Manifest.Id, "Schema fixture", "Fixture", 0xE8A5,
            DiagnosticArea.Application, _ => true, (settings, _) => settings, _ => false);
        using var manager = new PluginManager([new(definition, () => source)], store);
        await manager.StartAsync();
        var model = (await manager.GetSettingsAsync(Manifest.Id))!;
        model.Settings.SetString("message", "live value");
        model.Settings.SetBoolean("enabled", true);
        Check(source.Context!.Settings.GetString("message") == "live value" && source.Context.Settings.GetBoolean("enabled")
            && source.Starts == 1, "Active instance reads schema writes immediately without restarting");
        await manager.ShutdownAsync();
        Check(source.Reason == PluginStopReason.Shutdown, "Shutdown forwards the optional stopping reason");
        foreach (var key in new[] { "", new string('x', 129) })
        {
            var rejected = false;
            try { source.Context.Settings.GetString(key); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Revoked context settings retain setting-key validation");
        }
    }

    private sealed class SchemaFixture : IMessageSource, IPluginSettingsSchema, IPluginLifecycleAware
    {
        public IPluginContext? Context;
        public int Starts;
        public PluginStopReason? Reason;
        public string Id => Manifest.Id;
        public string DisplayName => "Schema fixture";
        public IReadOnlyList<PluginSetting> SettingsSchema => [new("message", PluginSettingType.String, "Message"),
            new("enabled", PluginSettingType.Boolean, "Enabled", false)];
        public event Action<PluginNotification>? NotificationReceived { add { } remove { } }
        public Task InitializeAsync(IPluginContext context) { Context = context; return Task.CompletedTask; }
        public Task StartAsync() { Starts++; return Task.CompletedTask; }
        public Task OnStoppingAsync(PluginStopReason reason) { Reason = reason; return Task.CompletedTask; }
        public Task StopAsync() => Task.CompletedTask;
        public void Dispose() { }
    }

    private static async Task CheckConcurrency(string root)
    {
        var paths = new PluginPaths(Path.Combine(root, "concurrent"));
        var store = new SettingsStore(Path.Combine(root, "concurrent-settings.json"));
        using var manager = new PluginManager([], store);
        manager.ConfigureDirectories(paths);
        await manager.StartAsync();
        var installer = new PluginInstaller(paths);
        var notification = new IslandNotification(1, DateTimeOffset.UtcNow, "Fixture", "Fixture", "message");
        var callbacks = 0;
        manager.Received += n => { _ = manager.Snapshot(); _ = manager.Allows(n); Interlocked.Increment(ref callbacks); };
        using var cancel = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 4).Select(reader => Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested)
            {
                _ = manager.Snapshot();
                manager.Allows(notification);
                await Task.Yield();
            }
        })).ToArray();
        try
        {
            for (var cycle = 0; cycle < 6; cycle++)
            {
                using var prepared = await installer.PrepareAsync(PluginInstallationChecks.Zip(root, Manifest));
                await manager.InstallAsync(prepared);
                await Task.WhenAll(manager.RescanAsync(), Task.Run(() => manager.Snapshot()));
                if (cycle == 0) await Task.Delay(400);
                await manager.RemoveAsync(Manifest.Id);
            }
        }
        finally { cancel.Cancel(); await Task.WhenAll(readers); }
        Check(callbacks > 0 && manager.Snapshot().Count == 0, "Concurrent snapshots and notification filtering survive real install/remove/rescan and callback reentry");
        await manager.ShutdownAsync();
    }
}
