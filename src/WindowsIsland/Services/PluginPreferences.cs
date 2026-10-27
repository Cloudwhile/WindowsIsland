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

    // 新秘密采用对象信封，与合法普通字符串完全分离；解密失败不降级为普通字符串。
    internal void EnsurePluginSecret(string id, string key)
    {
        ValidatePluginKey(key);
        lock (_gate)
        {
            if (PluginValue(id, key) is not JsonValue node || !node.TryGetValue<string>(out var legacy)) return;
            var migrated = PluginSecretProtection.MigrateLegacy(id, key, legacy);
            SetPluginValue(id, key, SecretEnvelope(migrated.Ciphertext, migrated.RequiresReset));
        }
    }

    internal string? GetPluginSecret(string id, string key, string? fallback)
    {
        ValidatePluginKey(key);
        lock (_gate)
        {
            if (PluginValue(id, key) is not JsonObject envelope || envelope["version"] is not JsonValue version
                || !version.TryGetValue<int>(out var number) || number != 1
                || envelope["kind"] is not JsonValue kind || !kind.TryGetValue<string>(out var format) || format != "dpapi-current-user"
                || envelope["requiresReset"] is not JsonValue reset || !reset.TryGetValue<bool>(out var invalid) || invalid
                || envelope["ciphertext"] is not JsonValue payload || !payload.TryGetValue<string>(out var ciphertext)) return fallback;
            return PluginSecretProtection.Unprotect(id, key, ciphertext) ?? fallback;
        }
    }

    internal void SetPluginSecret(string id, string key, string? value) => SetPluginValue(id, key,
        value is null ? null : SecretEnvelope(PluginSecretProtection.Protect(id, key, value), false));

    private static JsonObject SecretEnvelope(string ciphertext, bool requiresReset) => new()
    {
        ["version"] = 1, ["kind"] = "dpapi-current-user", ["ciphertext"] = ciphertext, ["requiresReset"] = requiresReset
    };

    private JsonNode? PluginValue(string id, string key) => _pluginPreferences[id] is JsonObject values ? values[key] : null;

    internal bool HasPluginValue(string id, string key)
    {
        ValidatePluginKey(key);
        lock (_gate) return PluginValue(id, key) is not null;
    }

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
