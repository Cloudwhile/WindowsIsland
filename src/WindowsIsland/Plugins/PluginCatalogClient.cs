using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.Services;

namespace WindowsIsland.Plugins;


internal sealed class PluginCatalogClient : IDisposable
{
    public const string DefaultBaseUrl = "https://wini-plugins.abszero.cc/";
    public const string OfficialDeveloperId = "winiofficial";
    private readonly HttpClient _http;
    private readonly TimeSpan _requestTimeout;
    public PluginCatalogClient(HttpMessageHandler? handler = null, TimeSpan? requestTimeout = null)
    {
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(45);
        _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
            UseDefaultCredentials = false, Credentials = null }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<PluginCatalogFetch> FetchAsync(string address, CancellationToken token)
    {
        var baseUri = new Uri(PluginCatalogPolicy.BaseAddress(address));
        var sources = await JsonAsync<PluginSource>(new Uri(baseUri, "api/sources"), token);
        if (sources.Length > 512) throw new PluginException(PluginErrorCode.SourceList, "目录来源列表过大。", "C16");
        if (sources.Any(s => s is null)
            || sources.Select(s => s.DeveloperId).Distinct(StringComparer.Ordinal).Count() != sources.Length)
            throw new PluginException(PluginErrorCode.SourceList, "目录来源列表无效。", "C13");
        foreach (var source in sources) ValidateSource(source, baseUri);
        var result = new List<PluginSourceIndex>();
        var failures = new List<PluginException>();
        foreach (var source in sources)
        {
            try
            {
                var plugins = await JsonAsync<PluginCatalogEntry>(new Uri(source.IndexUrl), token);
                if (plugins.Length > 2048) throw new PluginException(PluginErrorCode.IndexSize, "插件索引过大。", "C16");
                foreach (var plugin in plugins) ValidateEntry(plugin, source);
                PluginCatalogPolicy.Directory([new(source, plugins)]);
                result.Add(new(source with { LastSync = DateTimeOffset.UtcNow }, plugins));
            }
            catch (PluginException error) { failures.Add(error); }
        }
        try { PluginCatalogPolicy.Directory(result); }
        catch (PluginException error) { failures.Add(error); result.Clear(); }
        return new(sources, result.ToArray(), failures.ToArray());
    }

    public static void ValidateSource(PluginSource source, Uri baseUri) => PluginCatalogPolicy.Source(source, baseUri);
    public static void ValidateEntry(PluginCatalogEntry plugin, PluginSource source) => PluginCatalogPolicy.Entry(plugin, source);

    private async Task<T[]> JsonAsync<T>(Uri uri, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_requestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new PluginException(PluginErrorCode.CatalogStatus, "目录请求未成功。", "C6");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            await CopyBoundedAsync(input, output, PluginCatalogPolicy.MaximumResponseBytes, timeout.Token);
            return PluginCatalogPolicy.Array<T>(output.ToArray());
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new PluginException(PluginErrorCode.CatalogTimeout, "目录请求超时。", "C10"); }
        catch (Exception error) when (error is HttpRequestException || error is IOException and not PluginException)
        { throw new PluginException(PluginErrorCode.CatalogFetch, "目录请求失败。", "C10"); }
    }

    public async Task DownloadAsync(PluginCatalogEntry plugin, string destination, CancellationToken token, string? githubProxy = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_requestTimeout);
        token = timeout.Token;
        var uri = GitHubDownloadProxy.Target(new Uri(plugin.DownloadUrl), githubProxy);
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
                    if (!GitHubDownloadProxy.AllowsRedirect(next, githubProxy))
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
            if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(plugin.Sha256.ToLowerInvariant()))) throw new PluginException(PluginErrorCode.Hash, "插件 SHA256 校验失败。", "C35");
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
            if (total > limit) throw new PluginException(PluginErrorCode.SourceSize, "目录响应超过大小限制。", "C8");
            await output.WriteAsync(buffer.AsMemory(0, length), token);
        }
    }

    public void Dispose() => _http.Dispose();
}

internal sealed record PluginCatalogFetch(PluginSource[] Listed, PluginSourceIndex[] Indices, PluginException[] Failures);
