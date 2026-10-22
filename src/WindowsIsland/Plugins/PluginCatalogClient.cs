using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

internal sealed record PluginSourceIndex(PluginSource Source, PluginCatalogEntry[] Plugins);

internal sealed class PluginCatalogClient : IDisposable
{
    public const string DefaultBaseUrl = "https://plugins.windowsisland.example/";
    public const string OfficialDeveloperId = "cloudwhile";
    private readonly HttpClient _http;
    public PluginCatalogClient(HttpMessageHandler? handler = null) => _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(45) };

    public static Uri BaseUri(string value)
    {
        if (!Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback)) throw new PluginException(PluginErrorCode.SourceAddress, "官方源地址无效。");
        return uri;
    }

    public async Task<PluginSourceIndex[]> FetchAsync(string baseUrl, CancellationToken token)
    {
        var baseUri = BaseUri(baseUrl);
        var sources = await JsonAsync<PluginSource[]>(new Uri(baseUri, "api/sources"), token);
        if (sources.Length > 512 || sources.Any(s => s is null) || sources.Select(s => s.DeveloperId).Distinct(StringComparer.Ordinal).Count() != sources.Length)
            throw new PluginException(PluginErrorCode.SourceList, "官方源列表无效。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PluginSourceIndex>();
        foreach (var source in sources)
        {
            ValidateSource(source, baseUri);
            var plugins = await JsonAsync<PluginCatalogEntry[]>(new Uri(source.IndexUrl), token);
            if (plugins.Length > 2048) throw new PluginException(PluginErrorCode.IndexSize, "官方插件索引过大。");
            foreach (var plugin in plugins)
            {
                ValidateEntry(plugin, source);
                if (!ids.Add(plugin.Id)) throw new PluginException(PluginErrorCode.IndexDuplicate, "官方插件标识重复。");
            }
            result.Add(new(source with { LastSync = DateTimeOffset.UtcNow }, plugins));
        }
        return result.ToArray();
    }

    public static void ValidateSource(PluginSource source, Uri baseUri)
    {
        if (!PluginManifestReader.DeveloperIdValid(source.DeveloperId) || string.IsNullOrWhiteSpace(source.DeveloperName)
            || source.DeveloperName.Length > 200 || !PluginManifestReader.GitHubRepository(source.RepositoryUrl)
            || !Uri.TryCreate(source.IndexUrl, UriKind.Absolute, out var index)
            || index != new Uri(baseUri, "api/sources/" + source.DeveloperId + "/plugins"))
            throw new PluginException(PluginErrorCode.SourceMetadata, "官方源元数据无效。");
    }

    public static void ValidateEntry(PluginCatalogEntry plugin, PluginSource source)
    {
        if (plugin is null || plugin.DeveloperId != source.DeveloperId || plugin.RepositoryUrl != source.RepositoryUrl
            || !PluginId.IsValid(plugin.Id) || plugin.Id.Length > 80 || PluginManifestReader.Reserved(plugin.Id)
            || string.IsNullOrWhiteSpace(plugin.Name) || plugin.Name.Length > 200 || plugin.Description is null || plugin.Description.Length > 8000
            || !ReleaseVersion.TryParse(plugin.Version, out _) || !ReleaseVersion.TryParse(plugin.MinHostVersion, out _)
            || plugin.ContractVersion < 1 || plugin.Sha256 is not { Length: 64 } || !plugin.Sha256.All(Uri.IsHexDigit)
            || plugin.Permissions is null || plugin.Permissions.Length > 64 || plugin.Permissions.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 80)
            || plugin.Permissions.Distinct(StringComparer.Ordinal).Count() != plugin.Permissions.Length
            || !Uri.TryCreate(plugin.DownloadUrl, UriKind.Absolute, out var download) || download.Scheme != "https" || download.Host != "github.com"
            || !download.IsDefaultPort || download.UserInfo.Length > 0 || download.Fragment.Length > 0 || download.Query.Length > 0
            || !download.AbsolutePath.StartsWith(new Uri(source.RepositoryUrl).AbsolutePath.TrimEnd('/') + "/releases/download/", StringComparison.Ordinal)
            || !download.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new PluginException(PluginErrorCode.IndexMetadata, "官方插件索引无效。");
    }

    private async Task<T> JsonAsync<T>(Uri uri, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        await CopyBoundedAsync(input, output, 4 * 1024 * 1024, token);
        return JsonSerializer.Deserialize<T>(output.ToArray(), PluginManifestReader.Json) ?? throw new PluginException(PluginErrorCode.SourceEmpty, "官方源返回空内容。");
    }

    public async Task DownloadAsync(PluginCatalogEntry plugin, string destination, CancellationToken token)
    {
        var uri = new Uri(plugin.DownloadUrl);
        try
        {
            HttpResponseMessage? response = null;
            for (var count = 0; count < 8; count++)
            {
                response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    var next = new Uri(uri, location);
                    response.Dispose();
                    response = null;
                    if (next.Scheme != "https" || !next.IsDefaultPort || next.UserInfo.Length != 0
                        || !(next.Host == "github.com" || next.Host.EndsWith(".githubusercontent.com", StringComparison.Ordinal)))
                        throw new PluginException(PluginErrorCode.DownloadRedirect, "插件下载重定向无效。");
                    uri = next;
                    continue;
                }
                break;
            }
            using (response)
            {
                if (response is null || response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("Plugin download failed.");
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    await CopyBoundedAsync(input, output, 256L * 1024 * 1024, token);
            }
            await using var file = File.OpenRead(destination);
            var hash = await SHA256.HashDataAsync(file, token);
            if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(plugin.Sha256))) throw new PluginException(PluginErrorCode.Hash, "插件 SHA256 校验失败。");
        }
        catch { File.Delete(destination); throw; }
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken token)
    {
        var buffer = new byte[65536];
        long total = 0;
        int length;
        while ((length = await input.ReadAsync(buffer, token)) > 0)
        {
            total += length;
            if (total > limit) throw new PluginException(PluginErrorCode.SourceSize, "官方源响应超过大小限制。");
            await output.WriteAsync(buffer.AsMemory(0, length), token);
        }
    }

    public void Dispose() => _http.Dispose();
}
