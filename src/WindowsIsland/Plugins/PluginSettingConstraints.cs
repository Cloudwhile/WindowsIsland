using System.Globalization;
using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

internal static class PluginSettingConstraints
{
    public static void Validate(PluginSetting field, string? value)
    {
        if (value?.Length > 4096 || field.Required && string.IsNullOrWhiteSpace(value)) throw Invalid(field.Key);
        if (string.IsNullOrEmpty(value)) return;
        if (field.Type == PluginSettingType.Integer && (!long.TryParse(value, NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var number) || number < field.Minimum || number > field.Maximum)) throw Invalid(field.Key);
        if (field.Type == PluginSettingType.Choice && field.Choices?.Contains(value, StringComparer.Ordinal) != true) throw Invalid(field.Key);
        if (field.Type == PluginSettingType.Url && (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Host.Length == 0)) throw Invalid(field.Key);
    }

    private static InvalidDataException Invalid(string key) => new("Invalid setting: " + key);
}
