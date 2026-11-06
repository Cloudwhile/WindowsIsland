namespace WindowsIsland.Contracts;

/// <summary>插件可选实现的设置声明，不依赖运行状态，不使用自定义 XAML。</summary>
/// <remarks>属性可在 InitializeAsync 之前读取。默认值和业务校验由插件提供；宿主只校验声明形状。</remarks>
public interface IPluginSettingsSchema
{
    /// <summary>获取动态 string/bool 设置项。扩展类型通过包内静态设置声明提供。</summary>
    IReadOnlyList<PluginSetting> SettingsSchema { get; }
}

/// <summary>与 IPluginSettings 对应的设置类型。</summary>
public enum PluginSettingType
{
    /// <summary>字符串，对应 GetString/SetString。</summary>
    String,
    /// <summary>布尔值，对应 GetBoolean/SetBoolean。</summary>
    Boolean,
    /// <summary>整数，以不含分隔符的十进制字符串读写。</summary>
    Integer,
    /// <summary>有限选项，以选项字符串读写。</summary>
    Choice,
    /// <summary>不含用户凭据的绝对 HTTPS URL。</summary>
    Url,
    /// <summary>宿主加密保存、界面遮蔽的字符串。</summary>
    Password
}

/// <summary>声明设置键（1–128 字符）、类型、标题和相应类型的默认值。</summary>
/// <param name="Key">插件命名空间内的设置键。</param>
/// <param name="Type">设置类型；扩展类型用于静态设置声明。</param>
/// <param name="Title">插件提供的显示标题。</param>
/// <param name="DefaultValue">字符串（允许 null）或布尔默认值；整数以十进制字符串表示；密码无默认值。</param>
public sealed record PluginSetting(string Key, PluginSettingType Type, string Title, object? DefaultValue = null)
{
    /// <summary>是否必须提供非空值。</summary>
    public bool Required { get; init; }
    /// <summary>整数下限（包含）。</summary>
    public long? Minimum { get; init; }
    /// <summary>整数上限（包含）。</summary>
    public long? Maximum { get; init; }
    /// <summary>枚举允许值，按大小写精确匹配。</summary>
    public string[]? Choices { get; init; }
}
