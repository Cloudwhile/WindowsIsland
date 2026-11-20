using System.Net;
using System.Text.Json.Nodes;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using static WindowsIsland.LogicTests.PluginCatalogContractChecks;

namespace WindowsIsland.LogicTests;

internal static class PluginCatalogV2Checks
{
    private static void Check(bool condition, string message) => PluginCatalogContractChecks.Check(condition, message);
    public static async Task RunAsync()
    {
        var second = Entry(id: "wini_plugin_second") with
        {
            RepositoryUrl = "https://github.com/another/second",
            DownloadUrl = "https://github.com/another/second/releases/download/v1.0.0/second.zip"
        };
        var transport = new Transport { Plugins = Json(new[] { Entry(), second }) };
        var paths = new PluginPaths(Root());
        using var catalog = new PluginCatalogService(paths, new PluginCatalogClient(transport));
        await catalog.ConfigureProxyAsync("https://proxy.example/");
        await catalog.RefreshAsync();
        Check(!catalog.State.Unavailable && catalog.State.AvailableSources.Single().Plugins.Length == 2
            && transport.Requests.Select(u => u.AbsolutePath).SequenceEqual(new[] { "/api/v2/sources", "/api/v2/sources/winiofficial/plugins" }),
            "V2: one developer lists plugins from different repositories with explicit v2 requests");
        var original = catalog.State.Sources.Single();
        var manifest = PluginDirectoryChecks.Manifest with { DeveloperId = "winiofficial", SourceKind = PluginSourceKind.Official };
        foreach (var invalid in new[] { second with { DeveloperId = "other" }, second with { RepositoryUrl = "https://github.com/another/second.git" },
            second with { RepositoryUrl = "https://github.com/another/../second" }, second with { RepositoryUrl = "https://github.com/another/second?x" },
            second with { RepositoryUrl = "https://gitlab.com/another/second" }, second with { DownloadUrl = Entry().DownloadUrl } })
        {
            transport.Plugins = Json(new[] { Entry(), invalid });
            await catalog.RefreshAsync();
            Check(catalog.State.Unavailable && ReferenceEquals(original, catalog.State.Sources.Single()) && !catalog.IsDelisted(manifest),
                "V2: invalid developer, repository or download rejects the entire index and preserves cache");
        }
        foreach (var url in new[] { Source().IndexUrl.Replace("/v2/", "/"), Source().IndexUrl + "?x", Source().IndexUrl + "/",
            Source().IndexUrl.Replace("winiofficial", "other"), Source().IndexUrl.Replace("/v2/", "/V2/") })
        {
            transport.Sources = Json(new[] { Source() with { IndexUrl = url } });
            var before = transport.Requests.Count;
            await catalog.RefreshAsync();
            Check(catalog.State.Failure == PluginErrorCode.SourceMetadata && transport.Requests.Count == before + 1
                && ReferenceEquals(original, catalog.State.Sources.Single()) && !catalog.IsDelisted(manifest),
                "V2 C15: only the exact v2 plugins URL is accepted, including when a v1 payload answers the v2 endpoint");
        }
        transport.Sources = Json(new[] { Source() });
        transport.Plugins = Json(new[] { Entry(), second });
        await catalog.RefreshAsync();
        var cachePath = Path.Combine(paths.Root, "sources.json");
        var validCache = await File.ReadAllTextAsync(cachePath);
        using (var restarted = new PluginCatalogService(paths))
        {
            await restarted.LoadAsync();
            Check(restarted.State.AvailableSources.Single().Plugins.Length == 2 && !restarted.State.Fresh
                && !restarted.IsDelisted(manifest), "V2: cross-repository cache reloads after revalidation without fresh removal evidence");
        }
        foreach (var marker in new JsonNode?[] { null, JsonValue.Create(1), JsonValue.Create("2") })
        {
            var json = JsonNode.Parse(validCache)!.AsObject();
            if (marker is null) json.Remove("catalogVersion"); else json["catalogVersion"] = marker.DeepClone();
            await File.WriteAllTextAsync(cachePath, json.ToJsonString());
            using var legacy = new PluginCatalogService(paths, new PluginCatalogClient(new Transport { Sources = "[]" }));
            await legacy.LoadAsync();
            Check(legacy.State.Sources.Length == 0 && legacy.State.Known is null && legacy.State.Listed is null
                && legacy.State.GitHubProxy == "https://proxy.example/", "V2: legacy cache cannot supply entries or removal history; proxy survives");
            await legacy.RefreshAsync();
            Check(!legacy.IsDelisted(manifest), "V2: first empty v2 response cannot use legacy known-developer history to invent delisting");
        }
        foreach (var mutation in new[] { "v1", "repository", "download", "developer" })
        {
            var json = JsonNode.Parse(validCache)!.AsObject();
            var source = json["sources"]![0]!["source"]!;
            var entry = json["sources"]![0]!["plugins"]![1]!;
            if (mutation == "v1") source["pluginsUrl"] = Source().IndexUrl.Replace("/v2/", "/");
            if (mutation == "repository") entry["repositoryUrl"] = "https://github.com/another/second.git";
            if (mutation == "download") entry["downloadUrl"] = Entry().DownloadUrl;
            if (mutation == "developer") entry["developerId"] = "other";
            await File.WriteAllTextAsync(cachePath, json.ToJsonString());
            using var damaged = new PluginCatalogService(paths);
            await damaged.LoadAsync();
            Check(damaged.State.Sources.Length == 0 && damaged.State.Known is null && !damaged.IsDelisted(manifest),
                "V2: protocol marker cannot bypass cached URL, repository, download or developer validation");
        }
        await File.WriteAllTextAsync(cachePath, validCache);
        using var unavailable = new PluginCatalogService(paths, new PluginCatalogClient(new Transport
            { Send = (_, _) => Task.FromResult(Response("[]", HttpStatusCode.ServiceUnavailable)) }));
        await unavailable.LoadAsync();
        await unavailable.RefreshAsync();
        Check(unavailable.State.AvailableSources.Single().Plugins.Length == 2 && !unavailable.IsDelisted(manifest),
            "V2: unavailable catalog preserves a revalidated cross-repository cache without delisting");
    }
}
