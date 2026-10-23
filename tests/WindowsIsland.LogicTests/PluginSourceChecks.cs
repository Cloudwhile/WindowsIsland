using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.LogicTests;

internal static class PluginSourceChecks
{
    private static void Check(bool value, string message) => PluginDirectoryChecks.Check(value, message);

    public static async Task RunAsync()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "plugin-sources", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new PluginPaths(Path.Combine(root, "Plugins"));
        using var mock = new CatalogMock();
        using var manager = new PluginManager([], new SettingsStore(Path.Combine(root, "settings.json")));
        manager.ConfigureDirectories(paths);
        await manager.StartAsync();
        var downloads = new GitHubProxy(mock.Base);
        using var library = new PluginLibrary(manager, paths, new PluginCatalogClient(new CatalogTransport(mock.Base)), new PluginCatalogClient(downloads));
        var manifest = PluginDirectoryChecks.Manifest with { RepositoryUrl = "https://github.com/example/plugins", Permissions = ["notifications", "settings", "process", "network"] };
        mock.Package = await File.ReadAllBytesAsync(PluginInstallationChecks.Zip(root, manifest));
        var entry = new PluginCatalogEntry(manifest.Id, manifest.Name, manifest.Version,
            "https://github.com/example/plugins/releases/download/v1.0.0/plugin.zip", Convert.ToHexStringLower(SHA256.HashData(mock.Package)),
            "0.0.0", PluginContract.Version, manifest.Permissions, "测试来源", manifest.RepositoryUrl, manifest.DeveloperId);
        mock.Sources = JsonSerializer.Serialize(new[] { new PluginSource("example", "示例开发者", manifest.RepositoryUrl, new Uri(new Uri(PluginCatalogClient.DefaultBaseUrl), "api/sources/example/plugins").AbsoluteUri) });
        mock.Plugins = JsonSerializer.Serialize(new[] { entry });
        await library.Catalog.RefreshAsync();
        await CheckProxyAsync(paths, library.Catalog);
        await CheckConcurrentProxyAsync(new PluginPaths(Path.Combine(root, "Concurrent")), mock.Base);
        var state = library.Catalog.State;
        Check(state.Fresh && state.Sources.Single().Plugins.Single().Id == entry.Id && !state.Unavailable,
            "A real local HTTP mock satisfies both fixed catalog API contracts");
        Check(JsonSerializer.Serialize(state.Sources[0].Source).Contains("\"pluginsUrl\"") && !JsonSerializer.Serialize(entry).Contains("review"),
            "Catalog wire fields are stable and contain no pending-review state");
        entry = state.Sources[0].Plugins[0];
        CheckInstallAvailability(entry, paths);
        await library.InstallOfficialAsync(entry, []);
        Check(downloads.LastUri?.Host == "proxy.example" && downloads.LastUri.AbsolutePath.StartsWith("/https://github.com/", StringComparison.Ordinal),
            "Configured GitHub proxy applies to release downloads without changing catalog identity");
        await library.Catalog.ConfigureProxyAsync("");
        var code = paths.Code(PluginSourceKind.Official, entry.Id);
        var external = manager.Snapshot().Single().Definition.Metadata!.External!;
        Check(PluginManifestReader.Read(code).SourceKind == PluginSourceKind.Official && external.Manifest.DeveloperId == "example"
            && !external.Trust.IsGranted("process") && !external.Trust.IsGranted("network"),
            "Catalog installation records official provenance without granting unconfirmed third-party permissions");
        Check(!library.Catalog.IsDelisted(external.Manifest), "A listed installed plugin is not delisted");

        manifest = manifest with { Version = "1.1.0" };
        mock.Package = await File.ReadAllBytesAsync(PluginInstallationChecks.Zip(root, manifest));
        entry = entry with { Version = manifest.Version, Sha256 = Convert.ToHexStringLower(SHA256.HashData(mock.Package)) };
        mock.Plugins = JsonSerializer.Serialize(new[] { entry });
        await library.Catalog.RefreshAsync();
        Check(library.Catalog.UpdateFor(external.Manifest)?.Version == "1.1.0", "Official updates compare semantic versions from the developer index");
        entry = library.Catalog.State.Sources[0].Plugins[0];
        var received = new TaskCompletionSource<IslandNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Received += notification => { if (notification.OriginProcessId.HasValue) received.TrySetResult(notification); };
        await library.InstallOfficialAsync(entry, ["process"]);
        external = manager.Snapshot().Single().Definition.Metadata!.External!;
        Check(external.Trust.IsGranted("process") && !external.Trust.IsGranted("network")
            && (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).OriginProcessId == Environment.ProcessId,
            "Only individually confirmed high risk permissions take effect through host capabilities");
        Check(PluginManifestReader.Read(code + ".bak").Version == "1.0.0", "Official update retains the previous code backup");

        mock.Fail = true;
        await library.Catalog.RefreshAsync();
        Check(library.Catalog.State.Unavailable && library.Catalog.State.Sources.Length == 1 && !library.Catalog.IsDelisted(external.Manifest),
            "A failed source request preserves cached listings and never invents a delisting");
        mock.Fail = false;
        mock.FailPlugins = true;
        await library.Catalog.RefreshAsync();
        Check(!library.Catalog.IsDelisted(external.Manifest), "A successful source list with a failed developer index cannot delist plugins");
        mock.FailPlugins = false;
        mock.Plugins = "[]";
        await library.Catalog.RefreshAsync();
        Check(library.Catalog.IsDelisted(external.Manifest) && library.Catalog.UpdateFor(external.Manifest) is null
            && manager.Snapshot().Single().StatusKey == "Connected" && Directory.Exists(code),
            "A successfully removed listing warns and excludes updates while the installed plugin keeps running");

        using (var restarted = new PluginCatalogService(paths, new PluginCatalogClient(new CatalogTransport(mock.Base))))
        {
            await restarted.LoadAsync();
            mock.Fail = true;
            await restarted.RefreshAsync();
            Check(restarted.State.BaseUrl == PluginCatalogClient.DefaultBaseUrl && !restarted.IsDelisted(external.Manifest),
                "Restarted cached configuration survives an unreachable service without new delisting flags");
        }
        mock.Fail = false;
        mock.Plugins = JsonSerializer.Serialize(new[] { entry with { Version = "1.2.0", Sha256 = new string('0', 64) } });
        await library.Catalog.RefreshAsync();
        var rejected = false;
        try { await library.InstallOfficialAsync(library.Catalog.State.Sources[0].Plugins[0], []); }
        catch (Exception error) when (error is PluginException or InvalidDataException) { rejected = true; }
        Check(rejected && PluginManifestReader.Read(code).Version == "1.1.0"
            && !Directory.EnumerateFiles(Path.Combine(paths.Root, ".downloads")).Any(),
            "SHA256 mismatch aborts installation, removes downloaded bytes and preserves running code");
        foreach (var invalid in new[] { entry with { DownloadUrl = "https://example.com/plugin.zip" }, entry with { DeveloperId = "other" },
            entry with { Sha256 = "invalid" }, entry with { Version = "1.0" } })
        {
            rejected = false;
            try { PluginCatalogClient.ValidateEntry(invalid, library.Catalog.State.Sources[0].Source); } catch (Exception error) when (error is PluginException or InvalidDataException) { rejected = true; }
            Check(rejected, "Invalid catalog metadata is rejected: " + invalid.DeveloperId + "/" + invalid.Sha256[..Math.Min(8, invalid.Sha256.Length)]);
        }
        Check(!PluginLibrary.Compatible(entry with { MinHostVersion = "999.0.0" }), "Catalog versions requiring a newer host cannot be installed");
        await manager.ShutdownAsync();
    }

    private static async Task CheckProxyAsync(PluginPaths paths, PluginCatalogService catalog)
    {
        var original = new Uri("https://github.com/example/plugins/releases/download/v1.0.0/plugin.zip");
        Check(GitHubDownloadProxy.Target(original, "") == original && GitHubDownloadProxy.Normalize("  ") == "",
            "An empty proxy connects directly to the original GitHub URL");
        foreach (var invalid in new[] { "http://proxy.example", "relative", "https://user:secret@proxy.example/", "https://proxy.example/?key=secret", "https://proxy.example/#fragment" })
        {
            var rejected = false;
            try { await catalog.ConfigureProxyAsync(invalid); } catch (PluginException e) { rejected = e.Code == PluginErrorCode.ProxyAddress; }
            Check(rejected && catalog.State.GitHubProxy == "", "Invalid proxy cannot replace the current configuration: " + invalid.Split('@').Last());
        }
        await catalog.ConfigureProxyAsync(" https://proxy.example ");
        using (var restarted = new PluginCatalogService(paths))
        {
            await restarted.LoadAsync();
            Check(restarted.State.GitHubProxy == "https://proxy.example/" && restarted.State.Sources.Length == catalog.State.Sources.Length,
                "A normalized GitHub proxy persists independently of cached catalog entries");
        }
        var cache = Path.Combine(paths.Root, "sources.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cache))!.AsObject();
        json["gitHubProxy"] = 17;
        File.WriteAllText(cache, json.ToJsonString());
        using (var damaged = new PluginCatalogService(paths))
        {
            await damaged.LoadAsync();
            Check(damaged.State.GitHubProxy == "" && damaged.State.Sources.Length == catalog.State.Sources.Length,
                "A damaged proxy field falls back to direct GitHub while retaining valid listings");
        }
        json["baseUrl"] = "http://untrusted.example/";
        File.WriteAllText(cache, json.ToJsonString());
        using (var changed = new PluginCatalogService(paths))
        {
            await changed.LoadAsync();
            Check(changed.State.Disabled && changed.State.BaseUrl == "http://untrusted.example/" && changed.State.Sources.Length == 0,
                "C3: an invalid stored base disables the catalog without a silent fallback");
        }
        Check(GitHubDownloadProxy.AllowsRedirect(new Uri("https://proxy.example/result"), "https://proxy.example/")
            && GitHubDownloadProxy.AllowsRedirect(new Uri("https://release-assets.githubusercontent.com/file"), "")
            && !GitHubDownloadProxy.AllowsRedirect(new Uri("https://untrusted.example/file"), "https://proxy.example/"),
            "Proxy downloads retain the original redirect restrictions and allow only the selected proxy origin");
        await catalog.ConfigureProxyAsync("https://proxy.example/");
    }

    private static async Task CheckConcurrentProxyAsync(PluginPaths paths, Uri mockBase)
    {
        var transport = new CatalogTransport(mockBase) { Pause = true };
        using var catalog = new PluginCatalogService(paths, new PluginCatalogClient(transport));
        var refreshing = catalog.RefreshAsync();
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            var first = catalog.ConfigureProxyAsync("https://first.example/");
            var last = catalog.ConfigureProxyAsync("https://last.example/");
            await Task.WhenAll(first, last).WaitAsync(TimeSpan.FromSeconds(3));
            Check(catalog.State.Busy && catalog.State.GitHubProxy == "https://last.example/",
                "Concurrent proxy saves serialize without waiting for a pending catalog request");
        }
        finally { transport.Resume.TrySetResult(); }
        await refreshing;
        using var restarted = new PluginCatalogService(paths);
        await restarted.LoadAsync();
        Check(catalog.State.Fresh && catalog.State.GitHubProxy == "https://last.example/"
            && restarted.State.GitHubProxy == "https://last.example/" && restarted.State.Sources.Length == 1,
            "A completed refresh preserves the newest proxy in memory and on disk");
    }

    private sealed class CatalogTransport(Uri mockBase) : HttpMessageHandler
    {
        private readonly HttpClient _client = new();
        public bool Pause;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri?.Host != new Uri(PluginCatalogClient.DefaultBaseUrl).Host) throw new InvalidOperationException("Expected the fixed catalog origin.");
            if (Pause) { Entered.TrySetResult(); await Resume.Task.WaitAsync(token); }
            return await _client.GetAsync(new Uri(mockBase, request.RequestUri.AbsolutePath), HttpCompletionOption.ResponseHeadersRead, token);
        }
        protected override void Dispose(bool disposing) { if (disposing) _client.Dispose(); base.Dispose(disposing); }
    }

    private static void CheckInstallAvailability(PluginCatalogEntry entry, PluginPaths paths)
    {
        PluginMetadata Installed(PluginSourceKind kind, string developer, string version = "1.0.0") => new(false, version, "fixture.dll",
            External: new ExternalPlugin(paths, new(entry.Id, entry.Name, version, "fixture.dll", "Fixture",
                PluginContract.Version, [], DeveloperId: developer, SourceKind: kind), paths.Code(kind, entry.Id)));
        var official = Installed(PluginSourceKind.Official, entry.DeveloperId);
        Check(PluginLibrary.InstallUnavailableReason(entry, null) is null, "A compatible uninstalled catalog entry can be installed");
        Check(PluginLibrary.InstallUnavailableReason(entry, official) == "PluginAlreadyLatest", "The same installed version explains the disabled catalog action");
        Check(PluginLibrary.InstallUnavailableReason(entry with { Version = "0.9.0" }, official) == "", "An older catalog version cannot downgrade the installed plugin");
        Check(PluginLibrary.InstallUnavailableReason(entry, Installed(PluginSourceKind.Manual, entry.DeveloperId)) == "PluginInstalledElsewhere",
            "A manual installation explains why catalog replacement is unavailable");
        Check(PluginLibrary.InstallUnavailableReason(entry, Installed(PluginSourceKind.Official, "other")) == "PluginDeveloperMismatch",
            "A different catalog owner explains the disabled action");
        Check(PluginLibrary.InstallUnavailableReason(entry with { ContractVersion = PluginContract.Version + 1 }, null) == "PluginContractIncompatible",
            "Incompatible contract requirements explain the disabled action");
        Check(PluginLibrary.InstallUnavailableReason(entry with { MinHostVersion = "999.0.0" }, null) == "PluginHostTooOld",
            "A newer host requirement explains the disabled action");
        foreach (var installed in new PluginMetadata?[] { null, official, Installed(PluginSourceKind.Manual, entry.DeveloperId),
            Installed(PluginSourceKind.Official, "other"), new(false, "1.0.0", "fixture.dll") })
        foreach (var version in new[] { "0.9.0", "1.0.0", "1.1.0" })
        foreach (var contract in new[] { PluginContract.Version, PluginContract.Version + 1 })
        foreach (var host in new[] { "0.0.0", "999.0.0" })
        {
            var candidate = entry with { Version = version, ContractVersion = contract, MinHostVersion = host };
            var newer = installed is null || ReleaseVersion.FromInformationalVersion(candidate.Version)
                .CompareTo(ReleaseVersion.FromInformationalVersion(installed.Version)) > 0;
            var sameOwner = installed?.External is not { } external || external.Manifest.SourceKind == PluginSourceKind.Official
                && external.Manifest.DeveloperId == candidate.DeveloperId;
            Check((PluginLibrary.InstallUnavailableReason(candidate, installed) is null) == (newer && sameOwner && PluginLibrary.Compatible(candidate)),
                $"Catalog explanations preserve eligibility: {installed?.External?.Manifest.SourceKind}/{version}/{contract}/{host}");
        }
    }

    private sealed class GitHubProxy(Uri mockBase) : HttpMessageHandler
    {
        public Uri? LastUri;
        private readonly HttpClient _client = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            LastUri = request.RequestUri;
            if (request.RequestUri?.Host is not ("github.com" or "proxy.example")) throw new InvalidOperationException("Expected a GitHub release download or the selected proxy.");
            return _client.GetAsync(new Uri(mockBase, "package.zip"), HttpCompletionOption.ResponseHeadersRead, token);
        }
        protected override void Dispose(bool disposing) { if (disposing) _client.Dispose(); base.Dispose(disposing); }
    }

    private sealed class CatalogMock : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        public Uri Base { get; }
        public volatile bool Fail, FailPlugins;
        public volatile string Sources = "[]", Plugins = "[]";
        public byte[] Package = [];
        public CatalogMock()
        {
            var port = new TcpListener(IPAddress.Loopback, 0);
            port.Start();
            var number = ((IPEndPoint)port.LocalEndpoint).Port;
            port.Stop();
            Base = new Uri($"http://127.0.0.1:{number}/");
            _listener.Prefixes.Add(Base.AbsoluteUri);
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); } catch (Exception) { break; }
                    var path = context.Request.Url!.AbsolutePath;
                    var bytes = path == "/package.zip" ? Package : Encoding.UTF8.GetBytes(path == "/api/sources" ? Sources : Plugins);
                    context.Response.StatusCode = Fail || FailPlugins && path.EndsWith("/plugins", StringComparison.Ordinal) ? 503 : 200;
                    context.Response.ContentType = path == "/package.zip" ? "application/zip" : "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            });
        }
        public void Dispose() { _listener.Close(); _loop.GetAwaiter().GetResult(); }
    }
}
