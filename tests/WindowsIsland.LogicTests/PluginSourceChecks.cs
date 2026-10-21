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
        using var library = new PluginLibrary(manager, paths, downloads: new PluginCatalogClient(new GitHubProxy(mock.Base)));
        var manifest = PluginDirectoryChecks.Manifest with { RepositoryUrl = "https://github.com/example/plugins", Permissions = ["notifications", "settings", "process", "network"] };
        mock.Package = await File.ReadAllBytesAsync(PluginInstallationChecks.Zip(root, manifest));
        var entry = new PluginCatalogEntry(manifest.Id, manifest.Name, manifest.Version,
            "https://github.com/example/plugins/releases/download/v1.0.0/plugin.zip", Convert.ToHexString(SHA256.HashData(mock.Package)),
            "0.0.0", PluginContract.Version, manifest.Permissions, "测试来源", manifest.RepositoryUrl, manifest.DeveloperId);
        mock.Sources = JsonSerializer.Serialize(new[] { new PluginSource("example", "示例开发者", manifest.RepositoryUrl, new Uri(mock.Base, "api/sources/example/plugins").AbsoluteUri) });
        mock.Plugins = JsonSerializer.Serialize(new[] { entry });
        await library.Catalog.ConfigureAsync(mock.Base.AbsoluteUri);
        await library.Catalog.RefreshAsync();
        var state = library.Catalog.State;
        Check(state.Fresh && state.Sources.Single().Plugins.Single().Id == entry.Id && !state.Unavailable,
            "A real local HTTP mock satisfies both fixed catalog API contracts");
        Check(JsonSerializer.Serialize(state.Sources[0].Source).Contains("\"pluginsUrl\"") && !JsonSerializer.Serialize(entry).Contains("review"),
            "Catalog wire fields are stable and contain no pending-review state");
        entry = state.Sources[0].Plugins[0];
        await library.InstallOfficialAsync(entry, []);
        var code = paths.Code(PluginSourceKind.Official, entry.Id);
        var external = manager.Snapshot().Single().Definition.Metadata!.External!;
        Check(PluginManifestReader.Read(code).SourceKind == PluginSourceKind.Official && external.Manifest.DeveloperId == "example"
            && !external.Trust.IsGranted("process") && !external.Trust.IsGranted("network"),
            "Catalog installation records official provenance without granting unconfirmed third-party permissions");
        Check(!library.Catalog.IsDelisted(external.Manifest), "A listed installed plugin is not delisted");

        manifest = manifest with { Version = "1.1.0" };
        mock.Package = await File.ReadAllBytesAsync(PluginInstallationChecks.Zip(root, manifest));
        entry = entry with { Version = manifest.Version, Sha256 = Convert.ToHexString(SHA256.HashData(mock.Package)) };
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

        using (var restarted = new PluginCatalogService(paths))
        {
            await restarted.LoadAsync();
            mock.Fail = true;
            await restarted.RefreshAsync();
            Check(restarted.State.BaseUrl == mock.Base.AbsoluteUri && !restarted.IsDelisted(external.Manifest),
                "Restarted cached configuration survives an unreachable service without new delisting flags");
        }
        mock.Fail = false;
        mock.Plugins = JsonSerializer.Serialize(new[] { entry with { Version = "1.2.0", Sha256 = new string('0', 64) } });
        await library.Catalog.RefreshAsync();
        var rejected = false;
        try { await library.InstallOfficialAsync(library.Catalog.State.Sources[0].Plugins[0], []); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected && PluginManifestReader.Read(code).Version == "1.1.0"
            && !Directory.EnumerateFiles(Path.Combine(paths.Root, ".downloads")).Any(),
            "SHA256 mismatch aborts installation, removes downloaded bytes and preserves running code");
        foreach (var invalid in new[] { entry with { DownloadUrl = "https://example.com/plugin.zip" }, entry with { DeveloperId = "other" },
            entry with { Sha256 = "invalid" }, entry with { Version = "1.0" } })
        {
            rejected = false;
            try { PluginCatalogClient.ValidateEntry(invalid, library.Catalog.State.Sources[0].Source); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Invalid catalog metadata is rejected: " + invalid.DeveloperId + "/" + invalid.Sha256[..Math.Min(8, invalid.Sha256.Length)]);
        }
        Check(!PluginLibrary.Compatible(entry with { MinHostVersion = "999.0.0" }), "Catalog versions requiring a newer host cannot be installed");
        await manager.ShutdownAsync();
    }

    private sealed class GitHubProxy(Uri mockBase) : HttpMessageHandler
    {
        private readonly HttpClient _client = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri?.Host != "github.com") throw new InvalidOperationException("Expected a GitHub release download.");
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
