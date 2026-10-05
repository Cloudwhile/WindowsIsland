using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WindowsIsland.Services;

internal sealed record ReleaseAsset(string Name, Uri Download, long Size);
internal sealed record GitHubRelease(ReleaseVersion Version, string Tag, string Name, string Notes,
    Uri Page, bool IsPrerelease, ReleaseAsset Archive, ReleaseAsset Checksum,
    ReleaseAsset? Installer, ReleaseAsset? InstallerChecksum);

internal sealed class GitHubReleaseClient : IDisposable
{
    internal const string Repository = "Cloudwhile/WindowsIsland";
    internal const string DownloadPrefix = "https://github.com/" + Repository + "/releases/download/";
    internal const string ManifestUrl = "https://raw.githubusercontent.com/" + Repository + "/master/updates/releases.json";
    internal const int MaxManifestBytes = 4 * 1024 * 1024;
    private readonly HttpClient _http;

    public GitHubReleaseClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.MaxResponseContentBufferSize = MaxManifestBytes;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsIsland/" + ReleaseVersion.Current.Text);
    }

    public async Task<GitHubRelease?> FindUpdateAsync(ReleaseVersion current, bool includePrereleases,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        JsonDocument document;
        try { document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false); }
        catch (JsonException) { throw Localization.DataError("UpdateErrorManifest"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schema_version", out var schema) || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 1
                || !root.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array)
                throw Localization.DataError("UpdateErrorManifest");
            GitHubRelease? newest = null;
            foreach (var element in releases.EnumerateArray())
            {
                var release = ParseRelease(element);
                if (release is null || !includePrereleases && release.IsPrerelease || release.Version.CompareTo(current) <= 0) continue;
                if (newest is null || release.Version.CompareTo(newest.Version) > 0) newest = release;
            }
            return newest;
        }
    }

    internal static GitHubRelease? ParseRelease(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !Flag(element, "draft", out var draft) || draft
            || !Flag(element, "prerelease", out var prerelease)) return null;
        var tag = String(element, "tag_name");
        if (!tag.StartsWith('v') || !ReleaseVersion.TryParse(tag, out var version)) return null;
        if (!element.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        var name = $"WindowsIsland-{tag[1..]}-win-x64";
        var files = assets.EnumerateArray().Select(asset => ParseAsset(asset, tag)).OfType<ReleaseAsset>().ToArray();
        if (files.GroupBy(asset => asset.Name, StringComparer.Ordinal).Any(group => group.Count() > 1)) return null;
        var archive = files.FirstOrDefault(asset => asset.Name == name + ".zip");
        var checksum = files.FirstOrDefault(asset => asset.Name == name + ".zip.sha256");
        if (archive is null || checksum is null) return null;
        var page = new Uri($"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}");
        var title = String(element, "name");
        return new(version!, tag, title.Length == 0 ? tag : title, String(element, "body"), page, prerelease || version!.IsPrerelease,
            archive, checksum, files.FirstOrDefault(asset => asset.Name == name + ".msi"),
            files.FirstOrDefault(asset => asset.Name == name + ".msi.sha256"));
    }

    private static ReleaseAsset? ParseAsset(JsonElement element, string tag)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var name = String(element, "name");
        if (!Uri.TryCreate(String(element, "browser_download_url"), UriKind.Absolute, out var uri)
            || uri.AbsoluteUri != DownloadPrefix + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name)
            || !element.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number
            || !size.TryGetInt64(out var length) || length <= 0) return null;
        return new(name, uri, length);
    }

    private static bool Flag(JsonElement element, string property, out bool flag)
    {
        flag = false;
        if (!element.TryGetProperty(property, out var value)) return true;
        flag = value.ValueKind == JsonValueKind.True;
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static string String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    internal async Task<HttpResponseMessage> DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!uri.AbsoluteUri.StartsWith(DownloadPrefix, StringComparison.Ordinal))
            throw Localization.DataError("UpdateErrorAssetAddress");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try { EnsureSuccess(response); return response; }
        catch { response.Dispose(); throw; }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw Localization.Error(new HttpRequestException(Localization.Get("UpdateErrorRateLimit"), null, response.StatusCode), "UpdateErrorRateLimit");
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}
