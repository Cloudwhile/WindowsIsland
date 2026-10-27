using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.LogicTests;

internal static class PluginCatalogContractChecks
{
    internal static void Check(bool condition, string message) => PluginDirectoryChecks.Check(condition, message);
    internal static string Root() => Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "catalog-contract", Guid.NewGuid().ToString("N"));
    internal static PluginSource Source(string developer = "winiofficial", string? address = null) => new(developer, "开发者", "https://github.com/example/plugins",
        (address ?? PluginCatalogClient.DefaultBaseUrl) + "api/sources/" + developer + "/plugins");
    internal static PluginCatalogEntry Entry(string developer = "winiofficial", string id = "wini_plugin_fixture") => new(id, "示例插件", "1.0.0",
        "https://github.com/example/plugins/releases/download/v1.0.0/plugin.zip", new string('a', 64), "0.0.0", 1,
        ["notifications", "settings"], "", "https://github.com/example/plugins", developer);
    internal static string Json<T>(T value) => JsonSerializer.Serialize(value);
    internal sealed class Transport : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Send;
        public string Sources = Json(new[] { Source() });
        public string Plugins = Json(new[] { Entry() });
        public readonly List<Uri> Requests = [];
        public bool Hygienic = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.RequestUri!);
            Hygienic &= request.Method == HttpMethod.Get && request.Headers.CacheControl?.NoCache == true
                && request.Headers.All(h => h.Key.Equals("Cache-Control", StringComparison.OrdinalIgnoreCase))
                && !request.RequestUri!.OriginalString.Contains('?');
            return Send is not null ? Send(request, token) : Task.FromResult(Response(request.RequestUri!.AbsolutePath.EndsWith("/api/sources", StringComparison.Ordinal) ? Sources : Plugins));
        }
    }
    internal static HttpResponseMessage Response(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(json, Encoding.UTF8, "text/plain") }; // C9: Content-Type is deliberately not JSON.
    internal static bool Reject(Action action, PluginErrorCode? code = null)
    {
        try { action(); return false; } catch (PluginException e) { return code is null || e.Code == code; }
    }
    public static async Task RunAsync()
    {
        await PluginPlatformContractChecks.RunAsync();
        await ValidationAsync();
        await TransportAsync();
        await PluginCatalogCacheChecks.RunAsync();
        await PluginCatalogSecurityChecks.RunAsync();
    }
    private static async Task ValidationAsync()
    {
        var root = Root();
        var paths = new PluginPaths(root);
        var transport = new Transport();
        using var catalog = new PluginCatalogService(paths, new PluginCatalogClient(transport));
        await catalog.RefreshAsync();
        Check(catalog.State.Fresh && !catalog.State.Unavailable && catalog.State.AvailableSources.Single().Plugins.Single().Id == Entry().Id,
            "C7/C9/C13-C24/C33: legitimate winiofficial source and empty description pass with a non-JSON Content-Type");
        var original = catalog.State.Sources.Single();
        var manifest = PluginDirectoryChecks.Manifest with { DeveloperId = "winiofficial", SourceKind = PluginSourceKind.Official };
        async Task Invalid(string rule, string body, bool sources = false, PluginErrorCode? code = null)
        {
            transport.Sources = sources ? body : Json(new[] { Source() });
            transport.Plugins = sources ? Json(new[] { Entry() }) : body;
            await catalog.RefreshAsync();
            Check(catalog.State.Unavailable && ReferenceEquals(original, catalog.State.Sources.Single()) && !catalog.IsDelisted(manifest)
                && (code is null || catalog.State.Failure == code), rule + ": invalid response preserves the complete last index and installed listing state");
        }
        var valid = Json(new[] { Entry() });
        foreach (var body in new[] { "{}", "null", "[", valid.Replace("\"id\":", "\"id\":\"wini_plugin_duplicate\",\"id\":"),
            valid.Replace("\"name\":", "\"extra\":{\"x\":1,\"x\":2},\"name\":") })
            await Invalid("C7", body, code: PluginErrorCode.CatalogJson);
        await Invalid("C7", Json(new[] { Source() }).Replace("\"developerId\":", "\"developerId\":\"x\",\"developerId\":"), true);
        foreach (var token in new[] { "1.0", "\"1\"", "1e0", "-1", "0", "null", "true" })
            await Invalid("C21", valid.Replace("\"contractVersion\":1", "\"contractVersion\":" + token));
        transport.Plugins = valid.Replace("\"contractVersion\":1", "\"contractVersion\":999999999999999999999");
        await catalog.RefreshAsync();
        Check(!catalog.State.Unavailable && !PluginLibrary.Compatible(catalog.State.Sources.Single().Plugins.Single()), "C21/C25: positive JSON integers beyond Int32 remain valid incompatible contract majors");
        transport.Plugins = valid;
        await catalog.RefreshAsync();
        original = catalog.State.Sources.Single();
        var missing = JsonNode.Parse(valid)!.AsArray();
        missing[0]!.AsObject().Remove("description");
        await Invalid("C19", missing.ToJsonString());
        foreach (var mutation in new (string Rule, PluginCatalogEntry Entry)[]
        {
            ("C17", Entry() with { Id = "wini_plugin_countdown" }), ("C17", Entry() with { Id = "wini_plugin_System" }),
            ("C17", Entry() with { Id = "wini_plugin_1x" }), ("C17", Entry() with { Id = "wini_plugin_" + new string('a',69) }),
            ("C18", Entry() with { DeveloperId = "other" }), ("C18", Entry() with { RepositoryUrl = "https://github.com/Example/plugins" }),
            ("C19", Entry() with { Name = "" }), ("C19", Entry() with { Name = new string('a',201) }),
            ("C19", Entry() with { Description = new string('a',8001) }), ("C19", Entry() with { Description = null! }),
            ("C20", Entry() with { Version = "v1.0.0" }), ("C20", Entry() with { Version = "1.00.0" }),
            ("C20", Entry() with { Version = "1.0.0-01" }), ("C20", Entry() with { MinHostVersion = "1.0" }),
            ("C22", Entry() with { Permissions = ["invalid-permission"] }), ("C22", Entry() with { Permissions = ["Network"] }),
            ("C22", Entry() with { Permissions = ["network", "network"] }), ("C22", Entry() with { Permissions = [""] }),
            ("C22", Entry() with { Permissions = Enumerable.Repeat("network",65).ToArray() }), ("C22", Entry() with { Permissions = null! }),
            ("C23", Entry() with { Sha256 = new string('A',64) }), ("C23", Entry() with { Sha256 = new string('g',64) }),
            ("C23", Entry() with { Sha256 = new string('0',63) })
        }) await Invalid(mutation.Rule, Json(new[] { mutation.Entry }));
        foreach (var url in new[] { "http://github.com/example/plugins/releases/download/v1/a.zip",
            "https://github.com:444/example/plugins/releases/download/v1/a.zip", "https://user@github.com/example/plugins/releases/download/v1/a.zip",
            "https://@github.com/example/plugins/releases/download/v1/a.zip", "https://github.com.evil/example/plugins/releases/download/v1/a.zip", "https://github.com/other/plugins/releases/download/v1/a.zip",
            "https://github.com/example/plugins/releases/download/v1/a.zip?token=x", "https://github.com/example/plugins/releases/download/v1/a.zip#x",
            "https://github.com/example/plugins/releases/download/v1/a.zip/extra", "https://github.com/example/plugins/releases/download/v1/a.ZIP",
            "https://github.com/example/plugins/releases/download//a.zip", "https://github.com/example/plugins/releases/download/v%2F1/a.zip",
            "https://github.com/example/plugins/releases/download/v1/a%5cz.zip", "https://github.com/example/plugins/releases/download/v1/a%252f.zip",
            "https://github.com/example/plugins/releases/download/../a.zip", "https://github.com/example/plugins/releases/download/v1/..a.zip",
            "https://github.com/example/plugins/releases/download/%2e%2e/a.zip" })
            await Invalid("C24", Json(new[] { Entry() with { DownloadUrl = url } }));
        foreach (var source in new[] { Source() with { DeveloperId = "Winiofficial" }, Source() with { DeveloperId = "1example" },
            Source() with { DeveloperId = new string('a',65) }, Source() with { DeveloperName = "" }, Source() with { DeveloperName = new string('a',201) },
            Source() with { RepositoryUrl = "https://github.com/example/plugins/" }, Source() with { RepositoryUrl = "https://github.com/example/plugins.git" },
            Source() with { RepositoryUrl = "https://github.com/example/repo!" }, Source() with { RepositoryUrl = "https://github.com/example/plugins/more" },
            Source() with { RepositoryUrl = "https://github.com/example/plugins?x" }, Source() with { IndexUrl = Source().IndexUrl + "?x" },
            Source() with { IndexUrl = Source().IndexUrl.Replace("/api/", "/API/") } })
            await Invalid("C13/C14/C15", Json(new[] { source }), true);
        await Invalid("C13", Json(new[] { Source(), Source() }), true);
        await Invalid("C16", Json(Enumerable.Range(0,513).Select(i => Source("dev"+i))), true);
        await Invalid("C16", Json(Enumerable.Range(0,2049).Select(i => Entry(id: "wini_plugin_p"+i))));
        await Invalid("C17", Json(new[] { Entry(), Entry() }));
        await Invalid("C8", "[]" + new string(' ',PluginCatalogPolicy.MaximumResponseBytes-1), code: PluginErrorCode.SourceSize);
        transport.Sources = Json(new[] { Source() });
        transport.Plugins = "[]" + new string(' ',PluginCatalogPolicy.MaximumResponseBytes-2);
        await catalog.RefreshAsync();
        Check(!catalog.State.Unavailable && catalog.State.Sources.Single().Plugins.Length == 0, "C8: exactly 4 MiB is accepted without partial parsing");
        foreach (var version in new[] { "0.0.0", "1.2.3+build.009", "1.2.3-alpha.1+build", "999999999999999999999.0.0" })
            Check(CatalogVersion.TryParse(version, out _), "C20: legal SemVer accepted: " + version);
        foreach (var version in new[] { "01.0.0", "1.0.0-", "1.0.0+", "1.0.0-alpha..1", "1.0.0-01", "1.0.0\n" })
            Check(!CatalogVersion.TryParse(version, out _), "C20: invalid SemVer rejected: " + version.Trim());
        Check(CatalogVersion.Parse("1.0.0+a").CompareTo(CatalogVersion.Parse("1.0.0+b")) == 0
            && CatalogVersion.Parse("1.0.0-alpha.9").CompareTo(CatalogVersion.Parse("1.0.0-alpha.10")) < 0,
            "C20: build metadata is ignored and numeric prerelease identifiers compare numerically");
        PluginCatalogClient.ValidateEntry(Entry(id: "wini_plugin_a__"), Source());
        PluginCatalogClient.ValidateSource(Source("a__", "https://wini-plugins.abszero.cc/") with { DeveloperName = new string('a',200) }, new(PluginCatalogClient.DefaultBaseUrl));
        Check(true, "C13/C17: legal repeated and trailing underscores remain accepted");
    }
    private static async Task TransportAsync()
    {
        var transport = new Transport { Sources = "[]" };
        using var client = new PluginCatalogClient(transport);
        var empty = await client.FetchAsync(PluginCatalogClient.DefaultBaseUrl, default);
        Check(empty.Listed.Length == 0 && empty.Failures.Length == 0 && transport.Hygienic
            && transport.Requests.Single().AbsolutePath == "/api/sources", "C5/C6/C9/C12/C33: empty official directory is valid; requests are anonymous exact GETs with no-cache");
        transport.Sources = Json(Enumerable.Range(0,512).Select(i => Source("dev"+i)));
        transport.Plugins = "[]";
        var boundary = await client.FetchAsync(PluginCatalogClient.DefaultBaseUrl, default);
        Check(boundary.Listed.Length == 512 && boundary.Indices.Length == 512 && boundary.Failures.Length == 0,
            "C13/C16/C33: exactly 512 valid nonofficial sources and no official developer are accepted");
        foreach (var status in new[] { 201, 204, 301, 302, 304, 307, 400, 500 })
        {
            transport.Send = (_, _) => Task.FromResult(Response("[]", (HttpStatusCode)status));
            var before = transport.Requests.Count;
            try { await client.FetchAsync(PluginCatalogClient.DefaultBaseUrl, default); Check(false, "C6"); }
            catch (PluginException e) { Check(e.Code == PluginErrorCode.CatalogStatus && transport.Requests.Count == before+1, "C5/C6: non-200 is rejected without redirect or retry: " + status); }
        }
        transport.Send = (_, _) => throw new HttpRequestException("sensitive transport detail");
        var requests = transport.Requests.Count;
        try { await client.FetchAsync(PluginCatalogClient.DefaultBaseUrl, default); Check(false, "C10"); }
        catch (PluginException e) { Check(e.Code == PluginErrorCode.CatalogFetch && transport.Requests.Count == requests+1, "C10/C34: transient transport errors use stable code without retry"); }
        transport.Send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x5b,0xff,0x5d]) });
        try { await client.FetchAsync(PluginCatalogClient.DefaultBaseUrl, default); Check(false, "C7"); }
        catch (PluginException e) { Check(e.Code == PluginErrorCode.CatalogJson, "C7: invalid UTF-8 is rejected"); }
        using var timeout = new PluginCatalogClient(new Transport { Send = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Response("[]"); } }, TimeSpan.FromMilliseconds(30));
        try { await timeout.FetchAsync(PluginCatalogClient.DefaultBaseUrl, default); Check(false, "C10"); }
        catch (PluginException e) { Check(e.Code == PluginErrorCode.CatalogTimeout && e.Constraint == "C10", "C10/C34: request timeout has a stable code"); }
        using var perRequest = new PluginCatalogClient(new Transport { Send = async (request, token) =>
        {
            await Task.Delay(80,token);
            return Response(request.RequestUri!.AbsolutePath.EndsWith("/sources") ? Json(new[] { Source() }) : Json(new[] { Entry() }));
        } },TimeSpan.FromMilliseconds(140));
        var sequential = await perRequest.FetchAsync(PluginCatalogClient.DefaultBaseUrl, default);
        Check(sequential.Indices.Length == 1 && sequential.Failures.Length == 0, "C10: timeout applies to each request instead of the entire multi-request refresh");
    }
}
