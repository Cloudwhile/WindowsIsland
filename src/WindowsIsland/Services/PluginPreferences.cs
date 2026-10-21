using System.Text.Json.Nodes;
using WindowsIsland.Plugins;
using WindowsIsland.Contracts;

namespace WindowsIsland.Services;

internal sealed partial class SettingsStore
{
    private readonly HashSet<string> _removedPlugins = new(StringComparer.Ordinal);

    private void ReadPluginRemovals(JsonObject? root)
    {
        if (root?["RemovedPlugins"] is not JsonArray values) return;
        foreach (var node in values)
            if (node is JsonValue value && value.TryGetValue<string>(out var id) && PluginId.IsValid(id)
                && !BuiltInPluginIds.IsReserved(id))
                _removedPlugins.Add(id);
    }

    /// <summary>读取宿主保存的插件删除标记。</summary>
    public bool IsPluginRemoved(string id) { lock (_gate) return _removedPlugins.Contains(id); }

    public void MarkExternalRemoved(string id, bool removed)
    {
        lock (_gate)
        {
            var previous = _removedPlugins.Contains(id);
            if (removed) _removedPlugins.Add(id); else _removedPlugins.Remove(id);
            try { Persist(Current); }
            catch { if (previous) _removedPlugins.Add(id); else _removedPlugins.Remove(id); throw; }
        }
    }

    /// <summary>原子保存删除标记并清除该插件的设置，失败时恢复原内存状态。</summary>
    public void RemovePlugin(string id)
    {
        lock (_gate)
        {
            var previous = _pluginPreferences[id]?.DeepClone();
            var added = _removedPlugins.Add(id);
            _pluginPreferences.Remove(id);
            try { Persist(Current); }
            catch
            {
                if (previous is not null) _pluginPreferences[id] = previous;
                if (added) _removedPlugins.Remove(id);
                throw;
            }
        }
    }

    private void MigratePluginPreferences()
    {
        foreach (var pair in _pluginPreferences.ToArray())
        {
            var id = BuiltInPluginIds.FromLegacy(pair.Key);
            if (id == pair.Key || pair.Value is not JsonObject legacy) continue;
            if (_pluginPreferences[id] is not JsonObject current)
                _pluginPreferences[id] = legacy.DeepClone();
            else
                foreach (var setting in legacy)
                    if (!current.ContainsKey(setting.Key)) current[setting.Key] = setting.Value?.DeepClone();
            _pluginPreferences.Remove(pair.Key);
        }
    }

    public string? GetPluginString(string id, string key, string? fallback)
    {
        ValidatePluginKey(key);
        lock (_gate) return PluginValue(id, key) is JsonValue value && value.TryGetValue<string>(out var text) ? text : fallback;
    }

    public bool GetPluginBoolean(string id, string key, bool fallback)
    {
        ValidatePluginKey(key);
        lock (_gate) return PluginValue(id, key) is JsonValue value && value.TryGetValue<bool>(out var enabled) ? enabled : fallback;
    }

    public void SetPluginString(string id, string key, string? value) => SetPluginValue(id, key, value is null ? null : JsonValue.Create(value));
    public void SetPluginBoolean(string id, string key, bool value) => SetPluginValue(id, key, JsonValue.Create(value));

    private JsonNode? PluginValue(string id, string key) => _pluginPreferences[id] is JsonObject values ? values[key] : null;

    private void SetPluginValue(string id, string key, JsonNode? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ValidatePluginKey(key);
        lock (_gate)
        {
            if (_removedPlugins.Contains(id)) return;
            var previous = (JsonObject)_pluginPreferences.DeepClone();
            if (_pluginPreferences[id] is not JsonObject values) _pluginPreferences[id] = values = new();
            if (value is null) values.Remove(key);
            else values[key] = value;
            try { Persist(Current); }
            catch { _pluginPreferences = previous; throw; }
        }
    }

    internal static void ValidatePluginKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 128) throw new ArgumentException("Plugin setting keys must not exceed 128 characters.", nameof(key));
    }
}
