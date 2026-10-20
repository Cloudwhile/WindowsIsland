namespace WindowsIsland.Contracts;

/// <summary>定义插件标识的统一命名规则：固定前缀 wini、插件名称、固定类型后缀 plugin。</summary>
public static class PluginId
{
    /// <summary>获取插件标识的固定前缀。</summary>
    public const string Prefix = "wini_";

    /// <summary>获取插件标识的固定类型后缀。</summary>
    public const string Suffix = "_plugin";

    /// <summary>判断标识是否符合 wini_名称_plugin 格式。</summary>
    /// <param name="id">待检查的插件标识。</param>
    /// <returns>名称以小写英文字母开头，且仅包含小写英文字母、数字及分隔用下划线时为真。</returns>
    public static bool IsValid(string? id)
    {
        if (id is null || !id.StartsWith(Prefix, StringComparison.Ordinal) || !id.EndsWith(Suffix, StringComparison.Ordinal))
            return false;
        var length = id.Length - Prefix.Length - Suffix.Length;
        if (length <= 0) return false;
        var name = id.AsSpan(Prefix.Length, length);
        if (name[0] is < 'a' or > 'z' || name[^1] == '_') return false;
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9') continue;
            if (character != '_' || index == 0 || name[index - 1] == '_') return false;
        }
        return true;
    }
}
