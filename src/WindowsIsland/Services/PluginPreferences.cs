using System.Text.Json.Nodes;

namespace WindowsIsland.Services;

internal sealed partial class SettingsStore
{
    public string? GetPluginString(string id, string key, string? fallback)
    {
        lock (_gate) return PluginValue(id, key) is JsonValue value && value.TryGetValue<string>(out var text) ? text : fallback;
    }

    public bool GetPluginBoolean(string id, string key, bool fallback)
    {
        lock (_gate) return PluginValue(id, key) is JsonValue value && value.TryGetValue<bool>(out var enabled) ? enabled : fallback;
    }

    public void SetPluginString(string id, string key, string? value) => SetPluginValue(id, key, value is null ? null : JsonValue.Create(value));
    public void SetPluginBoolean(string id, string key, bool value) => SetPluginValue(id, key, JsonValue.Create(value));

    private JsonNode? PluginValue(string id, string key) => _pluginPreferences[id] is JsonObject values ? values[key] : null;

    private void SetPluginValue(string id, string key, JsonNode? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            var previous = (JsonObject)_pluginPreferences.DeepClone();
            if (_pluginPreferences[id] is not JsonObject values) _pluginPreferences[id] = values = new();
            if (value is null) values.Remove(key);
            else values[key] = value;
            try { Persist(Current); }
            catch { _pluginPreferences = previous; throw; }
        }
    }
}
