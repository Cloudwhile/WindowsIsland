namespace WindowsIsland.Contracts;

/// <summary>插件可选实现的设置声明，不依赖运行状态，不使用自定义 XAML。</summary>
/// <remarks>属性可在 InitializeAsync 之前读取。默认值和业务校验由插件提供；宿主只校验声明形状。</remarks>
public interface IPluginSettingsSchema
{
    /// <summary>获取 string/bool 设置项。标题由插件按自身语言策略提供。</summary>
    IReadOnlyList<PluginSetting> SettingsSchema { get; }
}

/// <summary>与 IPluginSettings 对应的设置类型。</summary>
public enum PluginSettingType
{
    /// <summary>字符串，对应 GetString/SetString。</summary>
    String,
    /// <summary>布尔值，对应 GetBoolean/SetBoolean。</summary>
    Boolean
}

/// <summary>声明设置键（1–128 字符）、类型、标题和相应类型的默认值。</summary>
/// <param name="Key">插件命名空间内的设置键。</param>
/// <param name="Type">字符串或布尔类型。</param>
/// <param name="Title">插件提供的显示标题。</param>
/// <param name="DefaultValue">字符串（允许 null）或布尔默认值。</param>
public sealed record PluginSetting(string Key, PluginSettingType Type, string Title, object? DefaultValue = null);
