using System.Globalization;
using System.Text.Json;
using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

internal static class PluginStaticSettings
{
    public const string FileName = "settings.schema.json";
    public static IReadOnlyList<PluginSetting>? Read(string directory)
    {
        var path = PluginPaths.Relative(directory, FileName);
        if (Directory.Exists(path)) throw Invalid("$");
        if (!File.Exists(path)) return null;
        try
        {
            using var input = PluginFileSafety.OpenSingleLinkRead(path);
            if (input.Length > 64 * 1024) throw Invalid("$");
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            Properties(root, "$", "schemaVersion", "fields");
            if (!root.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out var number) || number is not (1 or 2)
                || !root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array
                || fields.GetArrayLength() > 64) throw Invalid("$.schemaVersion / $.fields");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<PluginSetting>();
            foreach (var field in fields.EnumerateArray())
            {
                var at = "$.fields[" + result.Count + "]";
                Properties(field, at, number == 1 ? ["key", "type", "title", "default"]
                    : ["key", "type", "title", "default", "required", "min", "max", "choices", "secret"]);
                var key = Text(field, "key", 128, at);
                if (key.StartsWith("Host.", StringComparison.OrdinalIgnoreCase) || !keys.Add(key)) throw Invalid(at + ".key");
                var title = Text(field, "title", 200, at);
                var typeName = Text(field, "type", 16, at);
                var type = typeName switch
                {
                    "boolean" => PluginSettingType.Boolean, "string" => PluginSettingType.String,
                    "integer" when number == 2 => PluginSettingType.Integer,
                    "enum" when number == 2 => PluginSettingType.Choice,
                    "url" when number == 2 => PluginSettingType.Url,
                    "password" when number == 2 => PluginSettingType.Password,
                    _ => throw Invalid(at + ".type")
                };
                var required = Boolean(field, "required", at);
                var secret = Boolean(field, "secret", at);
                if (secret)
                {
                    if (type is not (PluginSettingType.String or PluginSettingType.Password)) throw Invalid(at + ".secret");
                    type = PluginSettingType.Password;
                }
                long? Bound(string name) => field.TryGetProperty(name, out var bound)
                    ? bound.TryGetInt64(out var value) ? value : throw Invalid(at + "." + name) : null;
                var minimum = Bound("min");
                var maximum = Bound("max");
                if ((minimum is not null || maximum is not null) && type != PluginSettingType.Integer || minimum > maximum) throw Invalid(at + ".min / .max");
                string[]? choices = null;
                if (field.TryGetProperty("choices", out var array))
                {
                    if (type != PluginSettingType.Choice || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 64) throw Invalid(at + ".choices");
                    choices = array.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
                        && value.GetString() is { Length: > 0 and <= 200 } text && !text.Any(char.IsControl) ? text : throw Invalid(at + ".choices")).ToArray();
                    if (choices.Distinct(StringComparer.Ordinal).Count() != choices.Length) throw Invalid(at + ".choices");
                }
                if (type == PluginSettingType.Choice && choices is null) throw Invalid(at + ".choices");
                var hasDefault = field.TryGetProperty("default", out var defaultValue);
                object? fallback = null;
                if (type == PluginSettingType.Boolean)
                {
                    if (!hasDefault || defaultValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid(at + ".default");
                    fallback = defaultValue.GetBoolean();
                }
                else if (type == PluginSettingType.Password)
                {
                    if (hasDefault) throw Invalid(at + ".default");
                }
                else if (hasDefault && defaultValue.ValueKind != JsonValueKind.Null)
                {
                    fallback = type == PluginSettingType.Integer
                        ? defaultValue.TryGetInt64(out var integer) ? integer.ToString(CultureInfo.InvariantCulture) : throw Invalid(at + ".default")
                        : defaultValue.ValueKind == JsonValueKind.String ? defaultValue.GetString() : throw Invalid(at + ".default");
                }
                var setting = new PluginSetting(key, type, title, fallback) { Required = required, Minimum = minimum, Maximum = maximum, Choices = choices };
                if (fallback is string textDefault)
                {
                    try { PluginSettingConstraints.Validate(setting, textDefault); }
                    catch (InvalidDataException) { throw Invalid(at + ".default"); }
                }
                result.Add(setting);
            }
            return result.AsReadOnly();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { throw Invalid("$"); }
    }

    private static bool Boolean(JsonElement element, string name, string at) => !element.TryGetProperty(name, out var value) ? false
        : value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw Invalid(at + "." + name);

    private static void Properties(JsonElement element, string at, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid(at);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal)) throw Invalid(at + "." + property.Name);
    }

    private static string Text(JsonElement element, string name, int limit, string at)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) throw Invalid(at + "." + name);
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > limit || text.Any(char.IsControl)) throw Invalid(at + "." + name);
        return text;
    }

    private static PluginException Invalid(string at) => new(PluginErrorCode.Manifest, "settings.schema.json " + at + ": 静态设置声明无效，请核对字段类型、范围和默认值。");
}
