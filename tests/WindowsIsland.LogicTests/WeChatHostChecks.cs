using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using System.IO.Compression;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.LogicTests;

internal static class WeChatHostChecks
{
    private static readonly PluginManifest Manifest = new("wini_plugin_wechat", "微信", "0.1.0", "Wini.Plugin.WeChat.dll",
        "Wini.Plugin.WeChat.WeChatPlugin", 1, ["notifications", "wechat"], DeveloperId: "winiofficial",
        RepositoryUrl: "https://github.com/WinIOfficial/wini_plugin_wechat", SourceKind: PluginSourceKind.Official);

    [ModuleInitializer]
    internal static void Run()
    {
        if (Environment.GetCommandLineArgs().Length == 1) CheckBrokerAsync().GetAwaiter().GetResult();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }

    private static async Task CheckBrokerAsync()
    {
        PluginManifestReader.Validate(Manifest);
        Check(!BuiltInPluginIds.IsReserved(Manifest.Id) && WeChatHostBroker.IsOfficial(Manifest), "WeChat is installable as a separate official plugin");
        foreach (var invalid in new[]
        {
            Manifest with { SourceKind = PluginSourceKind.Manual }, Manifest with { DeveloperId = "other" },
            Manifest with { Id = "wini_plugin_fake" }, Manifest with { RepositoryUrl = "https://github.com/other/wini_plugin_wechat" },
            Manifest with { Permissions = ["notifications"] }, Manifest with { Permissions = ["wechat"] }
        }) Check(!WeChatHostBroker.IsOfficial(invalid) && !PluginTrust.Create(invalid).IsGranted("wechat"),
            "Unverified identity or incomplete declaration cannot acquire WeChat host access");
        Check(PluginTrust.Create(Manifest).IsGranted("wechat") && !PluginTrust.Create(Manifest).IsGranted("process"),
            "WeChat capability does not grant direct process access");
        var backend = new Backend();
        var messages = new List<PluginNotification>();
        using var broker = new WeChatHostBroker(() => backend, action => action(), messages.Add);
        await broker.StartAsync(); await broker.StartAsync();
        Check(backend.Starts == 1, "Repeated broker start keeps a single listener baseline");
        var message = Message();
        backend.Emit(message); backend.Emit(message with { IsMetadataUpdate = true });
        Check(messages.Count == 2 && messages[0].DeduplicationKey == messages[1].DeduplicationKey
            && messages[0].DeduplicationKey != message.DeduplicationKey && messages[0].OriginProcessId == 42,
            "Trusted notifications keep activation and associate avatar updates with the same generation");
        backend.Allowed = false;
        Check(!broker.Allows(message), "Host-side conversation mute filtering remains synchronous");
        await broker.RetryAsync();
        Check(backend.Retries == 1, "Broker retry reaches only its owned backend");
        broker.Dispose(); broker.Dispose(); backend.Emit(message);
        Check(backend.Disposals == 1 && messages.Count == 2, "Revocation releases the listener exactly once and drops late messages");
        var queued = new Queue<Action>();
        using var pending = new WeChatHostBroker(() => throw new InvalidOperationException("Must not create"), queued.Enqueue, _ => { });
        var start = pending.StartAsync(); pending.Dispose(); queued.Dequeue()();
        var revoked = false;
        try { await start; } catch (ObjectDisposedException) { revoked = true; }
        Check(revoked, "A queued start cannot resurrect a revoked broker");
    }

    // 通过真实工作进程调用独立 ZIP；底层使用合成 backend，不读取正式微信。
    public static async Task RunPackageAsync(string zip)
    {
        await CheckBrokerAsync();
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "wechat-plugin-" + Guid.NewGuid().ToString("N"));
        var paths = new PluginPaths(root);
        var store = new SettingsStore(Path.Combine(root, "settings.json"));
        var backends = new List<Backend>();
        using var manager = new PluginManager([], store, backends: id =>
        {
            Check(id == Manifest.Id, "Only the WeChat backend is requested");
            var backend = new Backend(); backends.Add(backend); return backend;
        });
        manager.ConfigureDirectories(paths);
        var received = new List<IslandNotification>();
        manager.Received += received.Add;
        var consent = false;
        manager.ConfirmEnableAsync = descriptor => Task.FromResult(consent && descriptor.PluginId == Manifest.Id);
        ExternalPlugin? external = null;
        try
        {
            await manager.StartAsync();
            using (var manual = await new PluginInstaller(paths).PrepareAsync(zip))
                Check(!manual.Trust.IsGranted("wechat"), "Manual ZIP cannot self-assert the official WeChat capability");
            using var archive = ZipFile.OpenRead(zip);
            using var manifestReader = new StreamReader(archive.GetEntry("manifest.json")!.Open());
            using var packageManifest = JsonDocument.Parse(manifestReader.ReadToEnd());
            var packageVersion = packageManifest.RootElement.GetProperty("version").GetString();
            var catalogFile = Path.Combine(Path.GetDirectoryName(zip)!, "catalog-" + packageVersion + ".json");
            var entry = JsonSerializer.Deserialize<PluginCatalogEntry>(await File.ReadAllTextAsync(catalogFile), PluginManifestReader.Json)
                ?? throw new InvalidDataException();
            var bytes = await File.ReadAllBytesAsync(zip);
            Check(packageManifest.RootElement.GetProperty("minHostVersion").GetString() == entry.MinHostVersion
                && packageManifest.RootElement.GetProperty("minimumHostVersion").GetString() == entry.MinHostVersion,
                "Package and catalog declare the same minimum host version for the source and SDK");
            Check(entry.Sha256 == Convert.ToHexStringLower(SHA256.HashData(bytes)), "Generated official index matches the actual independent ZIP");
            using var library = new PluginLibrary(manager, paths,
                new PluginCatalogClient(new PackageCatalog(entry, bytes)), new PluginCatalogClient(new PackageCatalog(entry, bytes)));
            await library.Catalog.RefreshAsync();
            await library.InstallOfficialAsync(library.Catalog.State.Sources.Single().Plugins.Single(), []);
            external = manager.Snapshot().Single().Definition.Metadata!.External!;
            Check(PluginConsentProvenance.Verify(external), "Official catalog download records verified installation provenance");
            Check(manager.Snapshot().Single().StatusKey == "Off" && backends.Count == 0 && store.Current.WeChat,
                "Installing the official package preserves the existing preference and does not start listening");
            await manager.SetEnabledAsync(Manifest.Id, true);
            Check(backends.Count == 0, "Declining consent executes no listener");
            consent = true;
            await manager.SetEnabledAsync(Manifest.Id, true);
            Check(backends.Count == 1 && backends[0].Starts == 1 && manager.Snapshot().Single().StatusKey == "Connected",
                "The real LPAC worker starts the host listener through the restricted RPC");
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Wini.Plugin.WeChat"),
                "The host never loads the independent plugin assembly");
            backends[0].Emit(Message());
            Check(received.Single().OriginProcessId == 42 && received[0].AllowActivation,
                "Host-origin notification retains click activation without granting the worker process access");
            backends[0].Allowed = false;
            Check(!manager.Allows(received[0] with { Source = NotificationSource.SystemNotification }),
                "WeChat mute policy also filters the matching system notification");
            await manager.RetryAsync(Manifest.Id); await manager.PollAsync();
            Check(backends[0].Retries == 1, "Plugin retry crosses the real worker RPC once");
            await manager.SetEnabledAsync(Manifest.Id, false);
            Check(backends[0].Disposals == 1 && !store.Current.WeChat, "Disabling revokes the host listener and retains the legacy preference semantics");
            backends[0].Emit(Message()); Check(received.Count == 1, "Disabled generation cannot publish again");
            await manager.SetEnabledAsync(Manifest.Id, true);
            backends[1].Emit(Message());
            Check(received.Count == 2 && received[0].EventId != received[1].EventId, "Re-enable creates a new worker, listener and event generation");
            Directory.CreateDirectory(external.DataDirectory);
            File.WriteAllText(Path.Combine(external.DataDirectory, "keep.txt"), "保留数据");
            await manager.RemoveAsync(Manifest.Id);
            await external.VerifyUnloadAsync();
            Check(backends[1].Disposals == 1 && manager.Snapshot().Count == 0 && !Directory.Exists(external.Directory)
                && File.Exists(Path.Combine(external.DataDirectory, "keep.txt")), "Uninstall releases the listener and worker, removes code and preserves data");
        }
        finally
        {
            await manager.ShutdownAsync();
            if (external is not null) await external.VerifyUnloadAsync();
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.Root.ToUpperInvariant() + "\n" + Manifest.Id)))[..40].ToLowerInvariant();
            DeleteAppContainerProfile("WindowsIsland.Plugin." + digest);
        }
    }

    private static PluginNotification Message() => new(Manifest.Id, "会话", null, "合成测试消息", DateTimeOffset.UtcNow,
        DeduplicationKey: "uia/1", OriginProcessId: 42, AppName: "微信", AppId: "wechat", Kind: PluginNotificationKind.ClientAutomation);

    private sealed class PackageCatalog(PluginCatalogEntry entry, byte[] package) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var address = request.RequestUri?.AbsoluteUri;
            HttpContent content = address switch
            {
                PluginCatalogClient.DefaultBaseUrl + "api/sources" => new StringContent(JsonSerializer.Serialize(new[]
                {
                    new PluginSource(entry.DeveloperId, "winiofficial", entry.RepositoryUrl,
                        PluginCatalogClient.DefaultBaseUrl + "api/sources/" + entry.DeveloperId + "/plugins")
                }, PluginManifestReader.Json)),
                PluginCatalogClient.DefaultBaseUrl + "api/sources/winiofficial/plugins" =>
                    new StringContent(JsonSerializer.Serialize(new[] { entry }, PluginManifestReader.Json)),
                _ when address == entry.DownloadUrl => new ByteArrayContent(package),
                _ => throw new InvalidOperationException("Unexpected catalog request.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class Backend : IMessageSourceBackend, IPluginRetry
    {
        public int Starts, Disposals, Retries;
        public bool Allowed = true;
        public PluginStatus Status => new(PluginConnectionState.Connected);
        public bool CanRetry => true;
        public event Action<PluginNotification>? NotificationReceived;
        public Task StartAsync() { Starts++; return Task.CompletedTask; }
        public Task PollAsync() => Task.CompletedTask;
        public bool Allows(PluginNotification notification) => Allowed;
        public void Retry() => Retries++;
        public void Emit(PluginNotification notification) => NotificationReceived?.Invoke(notification);
        public void Dispose() => Disposals++;
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeleteAppContainerProfile(string name);
}
