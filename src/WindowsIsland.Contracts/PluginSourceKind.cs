using System.Text.Json.Serialization;

namespace WindowsIsland.Contracts;

/// <summary>表示插件的安装来源，由宿主根据实际安装槽确定。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PluginSourceKind>))]
public enum PluginSourceKind
{
    /// <summary>随宿主提供的内置插件。</summary>
    BuiltIn,
    /// <summary>从官方源安装的插件。</summary>
    Official,
    /// <summary>用户手动安装的插件。</summary>
    Manual
}
