using System.Text.Json.Serialization;

namespace WindowsIsland.Contracts;

/// <summary>插件包内的纯文本使用条款；宿主在启用前完整展示并记录同意版本。</summary>
/// <param name="Revision">条款修订标识。</param>
/// <param name="Text">UTF-8 纯文本正文，不是远程页面地址。</param>
public sealed record PluginTerms(string Revision, string Text);

/// <summary>插件通过宿主网络服务发送自有内容时的用途与范围披露。</summary>
/// <param name="Origins">允许请求的 HTTPS origin，不包含路径、查询或凭据。</param>
/// <param name="Purpose">网络使用目的。</param>
/// <param name="DataCategories">可能发送的数据类别。</param>
/// <param name="Notice">开发者或默认插件源提供的完整纯文本联网须知。</param>
public sealed record PluginNetworkDisclosure(
    [property: JsonPropertyName("origins")] string[] Origins,
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("dataCategories")] string[] DataCategories,
    [property: JsonPropertyName("notice")] string? Notice = null);
