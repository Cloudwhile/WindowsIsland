using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;
using static WindowsIsland.LogicTests.PluginCatalogContractChecks;

namespace WindowsIsland.LogicTests;

internal static class PluginCatalogCacheChecks
{
    private static void Check(bool condition, string message) => PluginCatalogContractChecks.Check(condition, message);
    public static async Task RunAsync()
    {
        await ConfigurationAsync();
        await StateAsync();
        await LimitsAsync();
    }
    private static async Task ConfigurationAsync()
    {
        var root = Root();
        var paths = new PluginPaths(root);
        Diagnostics.Initialize(Path.Combine(root,"diagnostics"), true);
        var transport = new Transport();
        using var catalog = new PluginCatalogService(paths, new PluginCatalogClient(transport));
        await catalog.RefreshAsync();
        var original = catalog.State.Sources;
        foreach (var address in new[] { "relative", "https://@example.com/", "http://example.com/", "https://user:secret@example.com/", "https://example.com/?x", "https://example.com/#x", "file:///test", "https://example.com/?", " https://example.com/" })
        {
            try { await catalog.ConfigureBaseAsync(address); Check(false, "C2"); }
            catch (PluginException e) { Check(e.Code == PluginErrorCode.SourceAddress && ReferenceEquals(original,catalog.State.Sources), "C2: invalid base cannot be saved or alter cache: " + address.Split('@').Last()); }
        }
        foreach (var address in new[] { "http://localhost:8080/", "http://127.0.0.1/", "http://[::1]/", "https://example.com/sub///" })
            Check(PluginCatalogPolicy.BaseAddress(address).EndsWith('/') && !PluginCatalogPolicy.BaseAddress(address).EndsWith("//"), "C2/C5: HTTPS or loopback HTTP base normalizes trailing slashes");
        await catalog.ConfigureBaseAsync("https://other.example/sub///");
        Check(catalog.State.BaseUrl == "https://other.example/sub/" && catalog.State.Sources.Length == 0 && catalog.State.Known is null, "C1/C4: changing the base immediately invalidates every index and delisting evidence");
        transport.Sources = Json(new[] { Source(address: catalog.State.BaseUrl) });
        await catalog.RefreshAsync();
        Check(!catalog.State.Unavailable && transport.Requests[^2].AbsoluteUri == "https://other.example/sub/api/sources"
            && transport.Requests[^1].AbsoluteUri == "https://other.example/sub/api/sources/winiofficial/plugins", "C1/C5: next refresh uses the new exact endpoints without restart");
        using (var restarted = new PluginCatalogService(paths))
        {
            await restarted.LoadAsync();
            Check(restarted.State.BaseUrl == catalog.State.BaseUrl && restarted.State.Sources.Length == 1 && !restarted.State.Fresh, "C1/C32: configured base and indefinite cache persist without stale successful-refresh evidence");
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Send = async (request, token) =>
        {
            entered.TrySetResult(); await resume.Task.WaitAsync(token);
            return Response(request.RequestUri!.AbsolutePath.EndsWith("/sources") ? transport.Sources : transport.Plugins);
        };
        var flight = catalog.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var merged = catalog.RefreshAsync();
        Check(ReferenceEquals(flight, merged), "C11: concurrent catalog refreshes share one flight");
        await catalog.ConfigureBaseAsync("https://new.example/");
        resume.TrySetResult(); await flight;
        Check(catalog.State.BaseUrl == "https://new.example/" && catalog.State.Sources.Length == 0
            && transport.Requests[^1].Host == "other.example", "C1/C4: in-progress refresh finishes at its old base and cannot repopulate the new cache");
        transport.Send = null;
        transport.Sources = Json(new[] { Source(address: catalog.State.BaseUrl) });
        await catalog.RefreshAsync();
        Check(catalog.State.Sources.Length == 1 && transport.Requests[^1].Host == "new.example", "C1: following refresh uses changed base");
        var cache = Path.Combine(paths.Root, "sources.json");
        foreach (var invalid in new JsonNode?[] { JsonValue.Create("http://unsafe.example/"), JsonValue.Create(17), null })
        {
            var json = JsonNode.Parse(await File.ReadAllTextAsync(cache))!.AsObject();
            json["baseUrl"] = invalid?.DeepClone();
            await File.WriteAllTextAsync(cache,json.ToJsonString());
            var fake = new Transport();
            using var damaged = new PluginCatalogService(paths, new PluginCatalogClient(fake));
            await damaged.LoadAsync();
            await damaged.RefreshAsync();
            Check(damaged.State.Disabled && damaged.State.Failure == PluginErrorCode.SourceAddress && fake.Requests.Count == 0
                && damaged.State.BaseUrl != PluginCatalogClient.DefaultBaseUrl, "C2/C3/C34: malformed stored base disables all directory requests and never falls back");
            await damaged.ConfigureProxyAsync("https://proxy.example/");
            Check(damaged.State.Disabled, "C3: changing a proxy does not repair an invalid base");
            await damaged.ConfigureBaseAsync(PluginCatalogClient.DefaultBaseUrl);
            await damaged.RefreshAsync();
            Check(!damaged.State.Disabled && !damaged.State.Unavailable, "C3: explicit valid base correction restores the directory");
        }
        var entries = Diagnostics.Store!.Snapshot();
        Check(entries.Any(e => e.Operation == "Catalog.C2" && e.Errors.Any(x => x.HResult == (PluginException.ErrorBase | (int)PluginErrorCode.SourceAddress))), "C34: invalid base records a stable diagnostic HResult and C number");
        var report = Diagnostics.Store.ReportJson();
        Check(!report.Contains("unsafe.example") && !report.Contains("secret") && !report.Contains(root.Replace("\\", "\\\\")), "C12/C34: catalog diagnostics contain no URL, credentials or installation path");
        foreach (var code in new[] { PluginErrorCode.SourceAddress, PluginErrorCode.CatalogFetch, PluginErrorCode.CatalogStatus, PluginErrorCode.CatalogJson,
            PluginErrorCode.SourceMetadata, PluginErrorCode.IndexDuplicate, PluginErrorCode.IndexMetadata, PluginErrorCode.Hash, PluginErrorCode.SourceSize, PluginErrorCode.CatalogTimeout })
        {
            var exception = new PluginException(code,"任意文本", "C34");
            foreach (var language in new[] { "zh-CN", "en-US" })
            {
                Localization.Configure(language);
                var key = PluginErrors.Key(exception);
                Check(key == PluginErrors.Key(new PluginException(code,"different message")) && Localization.Get(key) != key
                    && PluginErrors.DiagnosticKey(exception.HResult) == key, "C34: stable catalog error translates independently of exception text: " + code + "/" + language);
            }
        }
        Localization.Configure("zh-CN");
        Diagnostics.Shutdown();
    }
    private static async Task StateAsync()
    {
        var paths = new PluginPaths(Root());
        var transport = new Transport();
        var prereleases = false;
        using var catalog = new PluginCatalogService(paths, new PluginCatalogClient(transport), () => prereleases);
        var manifest = PluginDirectoryChecks.Manifest with { DeveloperId = "winiofficial", SourceKind = PluginSourceKind.Official };
        await catalog.RefreshAsync();
        var old = catalog.State.Sources.Single();
        Check(catalog.UpdateFor(manifest) is null && !catalog.IsDelisted(manifest), "C28: frozen same-version entry remains available for new installation and has no update");
        async Task Candidate(PluginCatalogEntry entry)
        { transport.Plugins = Json(new[] { entry }); await catalog.RefreshAsync(); }
        await Candidate(Entry() with { Version = "2.0.0+build" });
        Check(catalog.UpdateFor(manifest)?.Version == "2.0.0+build", "C26: same-id strictly newer compatible entry is an update");
        await Candidate(Entry() with { Version = "1.0.0+different" });
        Check(catalog.UpdateFor(manifest) is null, "C20/C26: build metadata alone never creates an update");
        await Candidate(Entry() with { Version = "0.9.0" });
        Check(catalog.UpdateFor(manifest) is null, "C29: older index offers no update or rollback");
        foreach (var entry in new[] { Entry() with { Version = "2.0.0", ContractVersion = 2 }, Entry() with { Version = "2.0.0", MinHostVersion = "999.0.0" } })
        {
            await Candidate(entry);
            Check(!catalog.State.Unavailable && catalog.State.Failure is null && catalog.UpdateFor(manifest) is null
                && catalog.State.Sources.Single().Plugins.Single().Version == "2.0.0", "C25: incompatibility excludes candidates while retaining a valid healthy index");
        }
        await Candidate(Entry() with { Version = "2.0.0-rc.1" });
        Check(catalog.UpdateFor(manifest) is null && !catalog.ChannelAllows(Entry() with { Version = "2.0.0-rc.1" }), "C27: stable channel never replaces an installed stable version with a prerelease");
        prereleases = true;
        Check(catalog.UpdateFor(manifest)?.Version == "2.0.0-rc.1", "C27: opt-in prerelease channel takes effect without refetching or restart");
        prereleases = false;
        transport.Plugins = "[]";
        await catalog.RefreshAsync();
        Check(catalog.IsDelisted(manifest) && catalog.UpdateFor(manifest) is null, "C30(a)/C31: a successful index missing an installed id establishes delisting and excludes updates");
        transport.Send = (_, _) => Task.FromResult(Response("[]", HttpStatusCode.ServiceUnavailable));
        await catalog.RefreshAsync();
        Check(!catalog.IsDelisted(manifest) && catalog.State.Sources.Single().Plugins.Length == 0, "C30/C32: a failed refresh retains even an empty cache without reusing old delisting evidence");
        transport.Send = null;
        transport.Plugins = Json(new[] { Entry() });
        await catalog.RefreshAsync();
        transport.Sources = "[]";
        await catalog.RefreshAsync();
        Check(catalog.IsDelisted(manifest) && catalog.State.AvailableSources.Length == 0 && catalog.State.Sources.Length == 1
            && !catalog.State.Unavailable, "C30(b)/C32/C33: source disappearance establishes delisting; the last good index is retained and an absent official source is valid");
        Check(!catalog.IsDelisted(manifest with { DeveloperId = "never_seen" }), "C30: a developer never seen in the catalog is never declared delisted");
        using (var restarted = new PluginCatalogService(paths))
        {
            await restarted.LoadAsync();
            Check(!restarted.IsDelisted(manifest) && restarted.State.AvailableSources.Length == 0, "C30/C32: restart retains removed-source cache without treating it as a fresh delisting observation");
        }
        transport.Sources = Json(new[] { Source(), Source("third") });
        transport.Send = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/sources") ? Response(transport.Sources)
            : request.RequestUri.AbsolutePath.Contains("/third/") ? Response("{}") : Response("[]"));
        await catalog.RefreshAsync();
        Check(catalog.IsDelisted(manifest) && catalog.State.Unavailable, "C30/C32: another source failing does not discard a successful developer index or its absence evidence");
        transport.Sources = Json(new[] { Source("third"), Source("fourth") });
        transport.Send = (request, _) => Task.FromResult(Response(request.RequestUri!.AbsolutePath.EndsWith("/sources") ? transport.Sources
            : Json(new[] { Entry(request.RequestUri.Segments[^2].TrimEnd('/')) })));
        await catalog.RefreshAsync();
        Check(catalog.IsDelisted(manifest) && catalog.State.Failure == PluginErrorCode.IndexDuplicate,
            "C17/C30(b): duplicate new indices preserve old caches while a separately valid source-list removal remains evidence");
    }
    private static async Task LimitsAsync()
    {
        var transport = new Transport();
        var sources = Enumerable.Range(0,5).Select(i => Source("dev"+i)).ToArray();
        transport.Sources = Json(sources);
        var overflow = false;
        var duplicate = false;
        transport.Send = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/sources")) return Task.FromResult(Response(transport.Sources));
            var developer = request.RequestUri.Segments[^2].TrimEnd('/');
            var n = int.Parse(developer[3..]);
            var count = n == 4 ? overflow ? 1 : 0 : 2048;
            return Task.FromResult(Response(Json(Enumerable.Range(0,count).Select(i => Entry(developer, "wini_plugin_p"+(duplicate ? i : n*2048+i))))));
        };
        using var catalog = new PluginCatalogService(new PluginPaths(Root()), new PluginCatalogClient(transport));
        await catalog.RefreshAsync();
        Check(!catalog.State.Unavailable && catalog.State.AvailableSources.Sum(s=>s.Plugins.Length)==8192, "C16: exactly 8192 plugins across valid per-source indices are accepted");
        var indices = catalog.State.Sources;
        overflow = true;
        await catalog.RefreshAsync();
        Check(catalog.State.Failure == PluginErrorCode.IndexSize && ReferenceEquals(indices,catalog.State.Sources), "C16/C32: 8193 total plugins reject the refresh without changing cached indices");
        overflow = false; duplicate = true;
        await catalog.RefreshAsync();
        Check(catalog.State.Failure == PluginErrorCode.IndexDuplicate && ReferenceEquals(indices,catalog.State.Sources), "C17/C32: cross-source duplicate id rejects the refresh without changing cached indices");
    }
}
