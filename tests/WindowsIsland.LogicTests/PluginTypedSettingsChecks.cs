using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;
using static WindowsIsland.LogicTests.PluginDirectoryChecks;

namespace WindowsIsland.LogicTests;

internal static class PluginTypedSettingsChecks
{
    private static void Check(bool value, string message) => PluginDirectoryChecks.Check(value, message);
    private const string Schema = """
        {"schemaVersion":2,"fields":[
          {"key":"count","type":"integer","title":"数量","min":1,"max":10,"default":3},
          {"key":"choice","type":"enum","title":"选项","choices":["a","b"],"default":"a"},
          {"key":"endpoint","type":"url","title":"地址","required":true},
          {"key":"token","type":"password","title":"令牌","required":true},
          {"key":"optional","type":"string","title":"附加秘密","secret":true}]}
        """;

    public static async Task RunAsync()
    {
        PluginSecretMigrationChecks.Run();
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "typed-settings", Guid.NewGuid().ToString("N"));
        var paths = new PluginPaths(Path.Combine(root, "plugins"));
        var manifest = Manifest with { EntryType = "WindowsIsland.PluginFixture.SettingsChangedSource", Permissions = ["settings"] };
        var code = paths.Code(PluginSourceKind.Manual, manifest.Id);
        WritePlugin(code, manifest);
        var schema = Path.Combine(code, PluginStaticSettings.FileName);
        File.WriteAllText(schema, Schema);
        var fields = PluginStaticSettings.Read(code)!;
        Check(fields.Count == 5 && fields[0].DefaultValue as string == "3" && fields[4].Type == PluginSettingType.Password,
            "Static v2 normalizes integer defaults and secret strings without executing plugin code");
        foreach (var invalid in new[] {
            Schema.Replace("\"min\":1", "\"min\":11"), Schema.Replace("\"default\":3", "\"default\":30"),
            Schema.Replace("[\"a\",\"b\"]", "[\"a\",\"a\"]"), Schema.Replace("\"default\":\"a\"", "\"default\":\"c\""),
            Schema.Replace("\"type\":\"password\"", "\"type\":\"password\",\"default\":null"),
            Schema.Replace("\"type\":\"url\"", "\"type\":\"url\",\"default\":\"http://example.com\""),
            Schema.Replace("\"required\":true", "\"required\":\"true\"") })
        {
            File.WriteAllText(schema, invalid);
            var rejected = false;
            try { PluginStaticSettings.Read(code); } catch (PluginException error) { rejected = error.Message.Contains("$.fields["); }
            Check(rejected, "Invalid v2 constraints report the static schema field path");
        }
        File.WriteAllText(schema, Schema);
        var upgradePath = Path.Combine(root, "upgrade.json");
        var upgradeStore = new SettingsStore(upgradePath);
        upgradeStore.SetPluginString(manifest.Id, "token", "synthetic-upgrade-secret");
        var upgraded = new PluginSettings(upgradeStore, manifest.Id, fields);
        Check(upgraded.GetString("token") == "synthetic-upgrade-secret"
            && !File.ReadAllText(upgradePath).Contains("synthetic-upgrade-secret"),
            "Changing a plain setting to a secret immediately replaces persisted plaintext with ciphertext");
        var store = new SettingsStore(Path.Combine(root, "settings.json"));
        var dispatch = new Queue<Action>();
        using var manager = new PluginManager([], store, action => { lock (dispatch) dispatch.Enqueue(action); });
        manager.ConfigureDirectories(paths);
        manager.ConfirmEnableAsync = _ => Task.FromResult(true);
        await manager.StartAsync();
        var model = (await manager.GetSettingsAsync(manifest.Id))!;
        var log = Path.Combine(paths.Data(manifest.Id), "changes.txt");
        await manager.SetEnabledAsync(manifest.Id, true);
        Check(!File.Exists(log), "Missing required settings block plugin initialization before DLL execution");
        await manager.SetEnabledAsync(manifest.Id, false);
        foreach (var pair in new[] { ("count", "0"), ("count", "1.5"), ("choice", "C"),
            ("endpoint", "http://example.com"), ("endpoint", "https://name:password@example.com"), ("token", "") })
        {
            var rejected = false;
            try { model.Settings.SetString(pair.Item1, pair.Item2); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Invalid typed or required values are rejected before persistence");
        }
        model.Settings.SetString("endpoint", "https://example.com/api");
        model.Settings.SetString("token", "synthetic-private-token");
        var external = manager.Snapshot().Single().Definition.Metadata!.External!;
        var savedPath = PluginConsentPolicy.HostSettingsPath(external);
        var text = File.ReadAllText(savedPath);
        Check(!text.Contains("synthetic-private-token") && text.Contains("dpapi:v1:"), "Secret preferences contain ciphertext only on disk");
        var persisted = new SettingsStore(savedPath);
        var reopened = new PluginSettings(persisted, manifest.Id, fields);
        Check(reopened.GetString("token") == "synthetic-private-token", "DPAPI secrets survive reopening the host store");
        var cipher = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(savedPath))!["Plugins"]![manifest.Id]!["token"]!["ciphertext"]!.GetValue<string>();
        Check(PluginSecretProtection.Unprotect(manifest.Id, "optional", cipher) is null
            && PluginSecretProtection.Unprotect("wini_plugin_other", "token", cipher) is null
            && PluginSecretProtection.Unprotect(manifest.Id, "token", "dpapi:v1:broken") is null,
            "Secret protection isolates plugin and key identities and rejects malformed ciphertext");
        await manager.SetEnabledAsync(manifest.Id, true);
        Check(File.Exists(log) && File.ReadAllText(log) == "Initialize\n", "An isolated plugin reads preconfigured secrets through host RPC");
        model.Settings.SetString("count", "7");
        while (dispatch.TryDequeue(out var firstAction)) firstAction();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.ReadAllText(log).Contains("Changed:count") && DateTime.UtcNow < deadline) await Task.Delay(25);
        Check(File.ReadAllText(log).Contains("Changed:count"), "Persisted changes reach the running LPAC instance with keys only");
        var previous = File.ReadAllText(log);
        model.Settings.SetString("count", "7");
        Check(dispatch.Count == 0, "Unchanged writes do not loop settings notifications");
        model.Settings.SetString("choice", "b");
        await manager.SetEnabledAsync(manifest.Id, false);
        while (dispatch.TryDequeue(out var action)) action();
        await manager.ShutdownAsync();
        Check(File.ReadAllText(log) == previous, "Disable discards queued changes for the revoked plugin instance");
    }
}
