using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace WindowsIsland.Services;

[SupportedOSPlatform("windows")]
internal sealed class RegistryWindowsPreferenceStore(RegistryKey user) : IWindowsPreferenceStore
{
    public string[] SubKeys(string path)
    {
        using var key = user.OpenSubKey(path);
        return key?.GetSubKeyNames() ?? [];
    }

    public PreferenceValue? Read(string path, string name)
    {
        using var key = user.OpenSubKey(path);
        var value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return null;
        return key!.GetValueKind(name) switch
        {
            RegistryValueKind.String when value is string text => new(PreferenceValueKind.String, Text: text),
            RegistryValueKind.DWord when value is int number => new(PreferenceValueKind.DWord, Number: number),
            RegistryValueKind.Binary when value is byte[] bytes => new(PreferenceValueKind.Binary, Bytes: bytes),
            _ => new(PreferenceValueKind.Unsupported)
        };
    }

    public void Write(string path, string name, PreferenceValue value)
    {
        using var key = user.CreateSubKey(path, writable: true) ?? throw new IOException("Could not open the preference key.");
        switch (value.Kind)
        {
            case PreferenceValueKind.String: key.SetValue(name, value.Text ?? "", RegistryValueKind.String); break;
            case PreferenceValueKind.DWord: key.SetValue(name, value.Number, RegistryValueKind.DWord); break;
            case PreferenceValueKind.Binary: key.SetValue(name, value.Bytes ?? [], RegistryValueKind.Binary); break;
            default: throw new ArgumentException("Unsupported registry value.", nameof(value));
        }
    }

    public void Remove(string path, string name)
    {
        using var key = user.OpenSubKey(path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

internal sealed class FileWindowsPreferenceStore : IWindowsPreferenceStore
{
    private readonly string _path;
    private readonly Dictionary<string, Dictionary<string, PreferenceValue>> _values;

    public FileWindowsPreferenceStore(string path)
    {
        _path = path;
        _values = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, PreferenceValue>>>(File.ReadAllText(path)) ?? [] : [];
        if (_values.Count == 0)
        {
            _values[NotificationBannerPreferences.SettingsKey + "\\WeChat"] = new()
            {
                ["ShowBanner"] = new(PreferenceValueKind.DWord, Number: 1),
                ["Enabled"] = new(PreferenceValueKind.DWord, Number: 1),
                ["ShowInActionCenter"] = new(PreferenceValueKind.DWord, Number: 1)
            };
            Save();
        }
    }

    public string[] SubKeys(string path)
    {
        lock (_values) return _values.Keys.Where(key => key.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase))
            .Select(key => key[(path.Length + 1)..].Split('\\')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public PreferenceValue? Read(string path, string name)
    {
        lock (_values) return _values.GetValueOrDefault(path)?.GetValueOrDefault(name);
    }

    public void Write(string path, string name, PreferenceValue value)
    {
        lock (_values)
        {
            if (!_values.TryGetValue(path, out var key)) _values[path] = key = [];
            key[name] = value;
            Save();
        }
    }

    public void Remove(string path, string name)
    {
        lock (_values) { _values.GetValueOrDefault(path)?.Remove(name); Save(); }
    }

    private void Save()
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_values));
        File.Move(temporary, _path, overwrite: true);
    }
}
