using System.Text.Json.Nodes;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.LogicTests;

internal static class PluginSecretMigrationChecks
{
    public static void Run()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "secret-migration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string id = "wini_plugin_secret_review";
        IReadOnlyList<PluginSetting> fields = [new("token", PluginSettingType.Password, "Token")];
        foreach (var plain in new[] { "synthetic-private-value", "dpapi:v1:synthetic-legacy-secret", "dpapi:v1:", "dpapi:v1:!" })
        {
            var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
            var store = new SettingsStore(path);
            store.SetPluginString(id, "token", plain);
            var settings = new PluginSettings(store, id, fields);
            var envelopeValue = JsonNode.Parse(File.ReadAllText(path))!["Plugins"]![id]!["token"];
            Check(settings.GetString("token") == plain && envelopeValue is JsonObject
                && envelopeValue["ciphertext"]!.GetValue<string>() != plain,
                "Plaintext migration preserves literal DPAPI-like prefixes inside a protected typed envelope");
            var reopened = new PluginSettings(new SettingsStore(path), id, fields);
            Check(reopened.GetString("token") == plain && store.GetPluginString(id, "token", null) is null,
                "Typed secrets survive reopening and are never returned by ordinary string storage");
        }
        var legacyPath = Path.Combine(root, "legacy.json");
        var legacyStore = new SettingsStore(legacyPath);
        var cipher = PluginSecretProtection.Protect(id, "token", "synthetic-migrated-secret");
        legacyStore.SetPluginString(id, "token", cipher);
        var legacy = new PluginSettings(legacyStore, id, fields);
        Check(legacy.GetString("token") == "synthetic-migrated-secret", "Valid legacy ciphertext upgrades without losing the secret");
        var saved = File.ReadAllText(legacyPath);
        _ = new PluginSettings(new SettingsStore(legacyPath), id, fields);
        Check(saved == File.ReadAllText(legacyPath), "Reopening a typed secret does not encrypt it again");

        var envelope = JsonNode.Parse(saved)!;
        envelope["Plugins"]![id]!["token"]!["ciphertext"] = "dpapi:v1:broken";
        File.WriteAllText(legacyPath, envelope.ToJsonString());
        var damaged = new PluginSettings(new SettingsStore(legacyPath), id, fields);
        Check(damaged.GetString("token") is null && File.ReadAllText(legacyPath) == envelope.ToJsonString(),
            "Corrupted typed ciphertext fails closed and is not reclassified or rewritten as plaintext");
        damaged.SetString("token", null);
        Check(JsonNode.Parse(File.ReadAllText(legacyPath))!["Plugins"]![id]!["token"] is null,
            "Clearing an unreadable optional secret removes its stored envelope");

        var ambiguousPath = Path.Combine(root, "ambiguous.json");
        var ambiguousStore = new SettingsStore(ambiguousPath);
        ambiguousStore.SetPluginString(id, "token", PluginSecretProtection.Protect(id, "other", "synthetic-wrong-key"));
        var ambiguous = new PluginSettings(ambiguousStore, id, fields);
        Check(ambiguous.GetString("token") is null && File.ReadAllText(ambiguousPath).Contains("requiresReset"),
            "Unverifiable legacy ciphertext is quarantined and requires explicit replacement");
        ambiguous.SetString("token", "replacement");
        Check(ambiguous.GetString("token") == "replacement", "Explicit secret replacement recovers an unverifiable legacy setting");
    }
    private static void Check(bool value, string message) => PluginDirectoryChecks.Check(value, message);
}
