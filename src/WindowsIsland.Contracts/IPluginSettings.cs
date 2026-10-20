namespace WindowsIsland.Contracts;

/// <summary>定义按插件标识隔离、支持并发调用的字符串和布尔设置存储。</summary>
/// <remarks>宿主绑定存储范围，插件只传入范围内的键，不能选择其他插件的存储。</remarks>
public interface IPluginSettings
{
    /// <summary>读取字符串设置；键不存在或类型无效时返回指定默认值。</summary>
    /// <param name="key">当前插件范围内的设置键。</param>
    /// <param name="defaultValue">缺少有效值时返回的默认值。</param>
    /// <returns>保存的字符串或默认值。</returns>
    string? GetString(string key, string? defaultValue = null);

    /// <summary>读取布尔设置；键不存在或类型无效时返回指定默认值。</summary>
    /// <param name="key">当前插件范围内的设置键。</param>
    /// <param name="defaultValue">缺少有效值时返回的默认值。</param>
    /// <returns>保存的布尔值或默认值。</returns>
    bool GetBoolean(string key, bool defaultValue = false);

    /// <summary>保存字符串设置，传入空引用时删除对应键。</summary>
    /// <param name="key">当前插件范围内的设置键。</param>
    /// <param name="value">要保存的字符串，或表示删除的空引用。</param>
    void SetString(string key, string? value);

    /// <summary>保存布尔设置。</summary>
    /// <param name="key">当前插件范围内的设置键。</param>
    /// <param name="value">要保存的布尔值。</param>
    void SetBoolean(string key, bool value);
}
