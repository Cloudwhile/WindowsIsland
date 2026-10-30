using System.IO.Compression;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.LogicTests;

internal static class TelegramHostChecks
{
    private static readonly PluginManifest Manifest = new("wini_plugin_telegram", "Telegram", "0.1.0", "Wini.Plugin.Telegram.dll",
        "Wini.Plugin.Telegram.TelegramPlugin", 1, ["notifications", "settings", "automation", "process"], DeveloperId: "winiofficial",
        RepositoryUrl: "https://github.com/WinIOfficial/wini_plugin_telegram", SourceKind: PluginSourceKind.Official)
    { Desktop = new(["Telegram"], ["Qt5Core.dll", "Qt6Core.dll"], "合成客户端桌面访问") };

    [ModuleInitializer]
    internal static void Run() { if (Environment.GetCommandLineArgs().Length == 1) CheckPolicy(); }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); Console.WriteLine("PASS " + message); }

    private static void CheckPolicy()
    {
        PluginManifestReader.Validate(Manifest);
        Check(!PluginManifestReader.Reserved(Manifest.Id), "Telegram is installable as an external identity");
        Check(PluginDesktopPolicy.IsAllowed(Manifest) && PluginTrust.Create(Manifest).IsGranted("automation")
            && PluginTrust.Create(Manifest).IsGranted("process"), "Verified adapter receives mediated desktop capabilities");
        foreach (var invalid in new[] { Manifest with { SourceKind = PluginSourceKind.Manual }, Manifest with { DeveloperId = "other" },
            Manifest with { Id = "wini_plugin_fake" }, Manifest with { RepositoryUrl = "https://github.com/other/repo" },
            Manifest with { Desktop = null }, Manifest with { Permissions = ["notifications", "automation"] } })
            Check(!PluginDesktopPolicy.IsAllowed(invalid) && !PluginTrust.Create(invalid).IsGranted("process"),
                "Unverified identity or incomplete desktop disclosure cannot grant process mediation");
        foreach (var scope in new[] { new PluginDesktopScope(["../client"], ["client.dll"], "scope"),
            new(["client", "CLIENT"], ["client.dll"], "scope"), new(["client"], [], "scope"), new(["client"], ["client.dll"], " ") })
        {
            var rejected = false;
            try { PluginManifestReader.Validate(Manifest with { Desktop = scope }); } catch (PluginException) { rejected = true; }
            Check(rejected, "Malformed or ambiguous desktop scopes are rejected");
        }
        Check(!PluginTrust.Create(Manifest with { Permissions = ["notifications", "wechat"] }).IsGranted("wechat"),
            "Retired application-specific permission does not grant a host listener");
    }

    public static async Task RunPackageAsync(string zip)
    {
        CheckPolicy();
        var paths = new PluginPaths(Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "desktop-" + Guid.NewGuid().ToString("N")));
        var store = new SettingsStore(Path.Combine(paths.Root, "settings.json"));
        Diagnostics.Initialize(Path.Combine(paths.Root,"diagnostics"),true);
        var services = new List<BridgeFixture>();
        using var manager = new PluginManager([], store, desktopFactory: _ => { var service = new BridgeFixture(); services.Add(service); return service; });
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
                Check(!manual.Trust.IsGranted("automation") && !manual.Trust.IsGranted("process"), "Manual import cannot self-assert desktop mediation");
            using var archive = ZipFile.OpenRead(zip);
            using var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open());
            var packageManifest = JsonSerializer.Deserialize<PluginManifest>(await reader.ReadToEndAsync(), PluginManifestReader.Json)!;
            store.Save(store.Current with { IncludePrereleaseUpdates = CatalogVersion.Parse(packageManifest.Version).IsPrerelease });
            var catalogPath = Path.Combine(Path.GetDirectoryName(zip)!, "catalog-" + packageManifest.Version + ".json");
            var entry = JsonSerializer.Deserialize<PluginCatalogEntry>(await File.ReadAllTextAsync(catalogPath), PluginManifestReader.Json)!;
            var bytes = await File.ReadAllBytesAsync(zip);
            Check(entry.MinHostVersion == packageManifest.MinimumHostVersion && entry.Sha256 == Convert.ToHexStringLower(SHA256.HashData(bytes)),
                "The generated catalog matches the actual package and minimum host version");
            using var library = new PluginLibrary(manager, paths, new PluginCatalogClient(new PackageCatalog(entry, bytes)),
                new PluginCatalogClient(new PackageCatalog(entry, bytes)), settings: store);
            await library.Catalog.RefreshAsync();
            await library.InstallOfficialAsync(library.Catalog.State.Sources.Single().Plugins.Single(), []);
            external = manager.Snapshot().Single().Definition.Metadata!.External!;
            Check(PluginConsentProvenance.Verify(external) && manager.Snapshot().Single().StatusKey == "Off" && services.Count == 0,
                "Catalog installation creates verified provenance without executing desktop operations");
            await manager.SetEnabledAsync(Manifest.Id, true);
            Check(services.Count == 0, "Declined enable consent executes no desktop service");
            consent = true;
            await manager.SetEnabledAsync(Manifest.Id, true);
            await UntilAsync(() => manager.Snapshot().Single().StatusKey == "Connected");
            Check(services.Count == 1 && services[0].Calls > 0, "The real LPAC worker calls the mediated desktop service");
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "Wini.Plugin.Telegram"),
                "The host never loads the external implementation assembly");
            services[0].Emit();
            await UntilAsync(() => received.Count == 1);
            Check(received[0] is { OriginProcessId: 42, AllowActivation: true }, "Only a tracked client retains notification click activation");
            Check(received[0] is { Title: "合成会话", Body: "合成新消息", AppId: "telegram", Source: NotificationSource.ClientHook }
                && manager.Allows(received[0]), "The first Telegram bridge message passes the production routing filter");
            Check(received[0].SenderAvatar is { Length: > 0 }, "The LPAC worker encodes native avatar pixels without desktop framework dependencies");
            services[0].EmitDuplicate(); await Task.Delay(350);
            Check(received.Count == 1, "Repeated native sequence values do not replay notifications");
            var old = services[0];
            await manager.SetEnabledAsync(Manifest.Id, false);
            Check(old.Disposals == 1 && !store.Current.Telegram, "Disabling revokes desktop mediation and preserves legacy switch semantics");
            old.Emit(); await Task.Delay(650);
            Check(received.Count == 1, "A revoked generation cannot publish late messages");
            await manager.SetEnabledAsync(Manifest.Id, true);
            await UntilAsync(() => manager.Snapshot().Single().StatusKey == "Connected");
            services[1].Emit(); await UntilAsync(() => received.Count == 2);
            Check(received[0].EventId != received[1].EventId, "Re-enable establishes a distinct worker event generation");
            Directory.CreateDirectory(external.DataDirectory);
            File.WriteAllText(Path.Combine(external.DataDirectory,"keep.txt"),"保留数据");
            await manager.RemoveAsync(Manifest.Id); await external.VerifyUnloadAsync();
            Check(services[1].Disposals == 1 && manager.Snapshot().Count == 0 && !Directory.Exists(external.Directory)
                && File.Exists(Path.Combine(external.DataDirectory,"keep.txt")), "Uninstall releases worker and desktop access while retaining plugin data");
        }
        finally
        {
            await manager.ShutdownAsync();
            if (external is not null) await external.VerifyUnloadAsync();
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.Root.ToUpperInvariant()+"\n"+Manifest.Id)))[..40].ToLowerInvariant();
            DeleteAppContainerProfile("WindowsIsland.Plugin."+digest);
        }
        async Task UntilAsync(Func<bool> condition)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < deadline) { await manager.PollAsync(); if (condition()) return; await Task.Delay(100); }
            Console.WriteLine("DIAGNOSTICS "+JsonSerializer.Serialize(Diagnostics.Store?.Snapshot()));
            throw new TimeoutException("Desktop worker verification did not reach the expected state; status="
                +manager.Snapshot().SingleOrDefault()?.StatusKey+"; services="+services.Count
                +"; calls="+string.Join(",",services.Select(service=>service.Calls)));
        }
    }

    private sealed class BridgeFixture : IPluginDesktop, IPluginDesktopProcessScope, IDisposable
    {
        public int Calls, Disposals;
        private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _packets = new();
        private ulong _sequence;
        private bool _open;
        public void Emit() => _packets.Enqueue(Packet(1, ++_sequence));
        public void EmitDuplicate() => _packets.Enqueue(Packet(1, _sequence));
        public bool OwnsProcess(int id) => id == 42 && Disposals == 0;
        public void Dispose() { Interlocked.Increment(ref Disposals); _open = false; }
        public Task<DesktopClient[]> FindClientsAsync(CancellationToken token = default)
        {
            Interlocked.Increment(ref Calls);
            if (Disposals != 0) throw new UnauthorizedAccessException();
            return Task.FromResult<DesktopClient[]>([new(42, 1, "fixture", "7.3.3", [], [])]);
        }
        public Task<bool> OpenBridgeAsync(DesktopBridge request, CancellationToken token = default)
        {
            Check(request is { ProcessId: 42, Library: "WindowsIsland.TelegramHook.dll", Version: 2, Magic: 0x57495447 }
                && request.Sha256.Length == 64, "LPAC plugin requests its build-bound native payload");
            _open = true; _packets.Enqueue(Packet(0, 0)); return Task.FromResult(true);
        }
        public Task<DesktopBridgeData> ReadBridgeAsync(int processId, CancellationToken token = default) =>
            Task.FromResult(new DesktopBridgeData(_open, _packets.TryDequeue(out var bytes) ? bytes : []));
        public Task<bool> CloseBridgeAsync(int processId, CancellationToken token = default) { _open = false; return Task.FromResult(true); }
        public Task<DesktopTree> ReadTreeAsync(int processId, long window, CancellationToken token = default) => throw new NotSupportedException();
        public Task<byte[]?> ReadImageAsync(DesktopImage request, CancellationToken token = default) => throw new NotSupportedException();
        public Task<byte[]> ReadModuleAsync(DesktopRead request, CancellationToken token = default) => throw new UnauthorizedAccessException();
        public Task<byte[]> ReadMemoryAsync(DesktopRead request, CancellationToken token = default) => throw new UnauthorizedAccessException();
        public Task<bool> SetFlagAsync(DesktopFlag request, CancellationToken token = default) => throw new UnauthorizedAccessException();
        private static byte[] Packet(uint kind, ulong sequence)
        {
            var title = kind == 1 ? "合成会话" : ""; var body = kind == 1 ? "合成新消息" : "";
            var textLength = (title.Length + body.Length) * 2;
            var avatarLength = kind == 1 ? 64 * 64 * 4 : 0;
            var bytes = new byte[56 + textLength + avatarLength];
            void U(int offset, uint value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
            U(0, 0x57495447); U(4, 2); U(8, kind); U(12, 42); U(40, (uint)title.Length); U(44, (uint)body.Length);
            U(48, (uint)avatarLength);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), sequence);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), DateTimeOffset.UtcNow.ToFileTime());
            Encoding.Unicode.GetBytes(title + body).CopyTo(bytes, 56);
            for (var pixel = 56 + textLength; pixel < bytes.Length; pixel += 4)
            { bytes[pixel] = 0x33; bytes[pixel + 1] = 0x99; bytes[pixel + 2] = 0xff; bytes[pixel + 3] = 0xff; }
            return bytes;
        }
    }

    private sealed class PackageCatalog(PluginCatalogEntry entry, byte[] package) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var address = request.RequestUri?.AbsoluteUri;
            HttpContent content = address switch
            {
                PluginCatalogClient.DefaultBaseUrl+"api/sources" => new StringContent(JsonSerializer.Serialize(new[] {
                    new PluginSource(entry.DeveloperId,"winiofficial",entry.RepositoryUrl,PluginCatalogClient.DefaultBaseUrl+"api/sources/"+entry.DeveloperId+"/plugins") },PluginManifestReader.Json)),
                PluginCatalogClient.DefaultBaseUrl+"api/sources/winiofficial/plugins" => new StringContent(JsonSerializer.Serialize(new[]{entry},PluginManifestReader.Json)),
                _ when address==entry.DownloadUrl=>new ByteArrayContent(package),
                _=>throw new InvalidOperationException("Unexpected catalog request.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=content});
        }
    }
    [DllImport("userenv.dll",CharSet=CharSet.Unicode)] private static extern int DeleteAppContainerProfile(string name);
}
