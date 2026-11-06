using System.Text.Json;
using System.Text.Json.Nodes;

namespace WindowsIsland.Plugins;

internal static class PluginSettingsMigration
{
    public static void EnsureMigrated(ExternalPlugin plugin)
    {
        var target = PluginConsentPolicy.HostSettingsPath(plugin);
        if (File.Exists(target)) return;
        var fields = new JsonObject();
        try
        {
            var source = Path.Combine(plugin.DataDirectory, "settings.json");
            if (File.Exists(source))
            {
                using var stream = PluginFileSafety.OpenSingleLinkRead(source);
                if (stream.Length > 2 * 1024 * 1024) throw new InvalidDataException();
                using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
                if (document.RootElement.TryGetProperty("Plugins", out var plugins)
                    && plugins.ValueKind == JsonValueKind.Object && plugins.TryGetProperty(plugin.Manifest.Id, out var own)
                    && own.ValueKind == JsonValueKind.Object)
                {
                    foreach (var field in own.EnumerateObject())
                    {
                        if (fields.Count >= 128) break;
                        if (string.IsNullOrWhiteSpace(field.Name) || field.Name.Length > 128
                            || field.Name.StartsWith("Host.", StringComparison.OrdinalIgnoreCase)) continue;
                        if (field.Value.ValueKind == JsonValueKind.String && field.Value.GetString() is { Length: <= 32768 } text)
                            fields[field.Name] = JsonValue.Create(text);
                        else if (field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            fields[field.Name] = JsonValue.Create(field.Value.GetBoolean());
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or PluginException or InvalidOperationException)
        { fields.Clear(); }
        // Store a snapshot once. Never execute plugin code, import another ID or host preferences,
        // follow links, or continue using the plugin-writable file as a privileged backing store.
        var snapshot = new JsonObject { ["Plugins"] = new JsonObject { [plugin.Manifest.Id] = fields } };
        if (!File.Exists(target)) PluginHostFiles.WriteAtomic(target, snapshot.ToJsonString());
    }
}
