namespace WindowsIsland.Contracts;

/// <summary>经宿主授权的网络服务；启用时确认开发者或默认插件源提供的联网须知。</summary>
public interface IPluginNetwork
{
    /// <summary>在已确认的联网须知范围内执行 HTTPS 请求。</summary>
    /// <param name="request">本次请求的数据和明确用途。</param>
    /// <param name="cancellationToken">取消本次请求。</param>
    /// <returns>有大小限制的 HTTP 响应。</returns>
    Task<PluginNetworkResponse> SendAsync(PluginNetworkRequest request, CancellationToken cancellationToken = default);
}

/// <summary>交由宿主按已授权范围发送的网络请求。</summary>
/// <param name="Url">已披露 origin 下的完整 HTTPS 地址。</param>
/// <param name="Method">HTTP 方法。</param>
/// <param name="Purpose">本次发送的具体用途。</param>
/// <param name="Headers">有限数量的显式请求头；宿主拒绝路由控制字段，不附加宿主凭据。</param>
/// <param name="Body">插件自有内容，最大 1 MiB。</param>
public sealed record PluginNetworkRequest(string Url, string Method, string Purpose,
    IReadOnlyDictionary<string, string>? Headers = null, byte[]? Body = null);

/// <summary>宿主网络服务返回的有界响应。</summary>
/// <param name="StatusCode">HTTP 状态码。</param>
/// <param name="Headers">有限数量的响应头。</param>
/// <param name="Body">响应正文，最大 2 MiB。</param>
public sealed record PluginNetworkResponse(int StatusCode, IReadOnlyDictionary<string, string> Headers, byte[] Body);

/// <summary>网络服务协议的固定上限。</summary>
public static class PluginNetworkLimits
{
    /// <summary>请求正文的最大字节数。</summary>
    public const int MaximumRequestBytes = 1024 * 1024;
    /// <summary>响应正文的最大字节数。</summary>
    public const int MaximumResponseBytes = 2 * 1024 * 1024;
    /// <summary>请求或响应头的最大数量。</summary>
    public const int MaximumHeaders = 32;
    /// <summary>单个头名称的最大字符数。</summary>
    public const int MaximumHeaderNameLength = 128;
    /// <summary>单个头值的最大字符数。</summary>
    public const int MaximumHeaderValueLength = 2048;
    /// <summary>请求地址的最大字符数。</summary>
    public const int MaximumUrlLength = 4096;
    /// <summary>用途的最大字符数。</summary>
    public const int MaximumPurposeLength = 1000;
}
