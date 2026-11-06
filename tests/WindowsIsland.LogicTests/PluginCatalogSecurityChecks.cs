using System.Security.Cryptography;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;
using static WindowsIsland.LogicTests.PluginCatalogContractChecks;

namespace WindowsIsland.LogicTests;

internal static class PluginCatalogSecurityChecks
{
    private static readonly string[] Permissions = ["notifications", "settings", "process", "network", "filesystem", "native", "automation"];
    private static void Check(bool condition, string message) => PluginCatalogContractChecks.Check(condition, message);
    public static async Task RunAsync()
    {
        await InstallAsync("winiofficial", true);
        await InstallAsync("cloudwhile", false);
        await InstallAsync("example", false);
        await InstallAsync("upstream", false, true);
        await InstallAsync("precedence", false, true, true);
    }
    private static async Task InstallAsync(string developer, bool official, bool sourceNotice = false, bool developerNotice = false)
    {
        var root = Root();
        var paths = new PluginPaths(Path.Combine(root,"Plugins"));
        var store = new SettingsStore(Path.Combine(root,"settings.json"));
        using var manager = new PluginManager([],store);
        manager.ConfigureDirectories(paths);
        await manager.StartAsync();
        var manifest = PluginDirectoryChecks.Manifest with { DeveloperId = developer, RepositoryUrl = Source().RepositoryUrl,
            Permissions = Permissions, Author = "作者", Network = new(["https://example.com"], "上传插件自有状态", ["插件状态"]) };
        if (sourceNotice && !developerNotice) manifest = manifest with { Network = null };
        var package = await File.ReadAllBytesAsync(PluginInstallationChecks.Zip(root,manifest));
        var transport = new Transport { Sources = Json(new[] { Source(developer) }) };
        var entry = Entry(developer) with { Permissions = Permissions, Sha256 = Convert.ToHexStringLower(SHA256.HashData(package)) };
        if (sourceNotice) entry = entry with { Network = new(["https://example.org"], "默认插件源提供的用途", ["插件自有内容"], "默认插件源提供的联网须知") };
        transport.Plugins = Json(new[] { entry });
        var downloads = new Transport { Send = (_, _) => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new System.Net.Http.ByteArrayContent(package) }) };
        using var library = new PluginLibrary(manager,paths,new PluginCatalogClient(transport),new PluginCatalogClient(downloads),settings: store);
        await library.Catalog.RefreshAsync();
        await library.InstallOfficialAsync(library.Catalog.State.AvailableSources.Single().Plugins.Single(),[]);
        var installed = manager.Snapshot().Single().Definition.Metadata!;
        if (sourceNotice)
        {
            var expected = developerNotice ? manifest.Network! : entry.Network!;
            var actual = PluginConsentPolicy.Describe(installed.External!).Network!;
            Check(actual.Purpose == expected.Purpose && actual.Notice == expected.Notice && actual.Origins.SequenceEqual(expected.Origins),
                "Default-source network notice persists across installation, with developer metadata taking precedence: " + developer);
            var disk = PluginManifestReader.Read(installed.External!.Directory);
            Check(disk.Network!.Purpose == expected.Purpose, "Source-supplied notice survives manifest reload without catalog availability");
        }
        Check(installed.External!.Trust.Granted.Order().SequenceEqual(new[] { "notifications", "settings", "network" }.Order())
            && manager.Snapshot().Single().StatusKey == "Off" && installed.Author == (official ? "winiofficial" : "作者"),
            "All catalog developers receive the same safe broker capabilities and remain disabled after installation: " + developer);
        if (official)
        {
            var hashDestination = Path.Combine(root,"uppercase.zip");
            using var hashClient = new PluginCatalogClient(new Transport { Send = downloads.Send });
            await hashClient.DownloadAsync(entry with { Sha256 = entry.Sha256.ToUpperInvariant() },hashDestination,default);
            Check(File.Exists(hashDestination), "C23/C35: hash comparison normalizes case while server entries still require lowercase");
            var code = installed.External.Directory;
            var data = installed.External.DataDirectory;
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data,"retain.txt"),"保留数据");
            manifest = manifest with { Version = "2.0.0" };
            package = await File.ReadAllBytesAsync(PluginInstallationChecks.Zip(root,manifest));
            entry = entry with { Version = "2.0.0", Sha256 = Convert.ToHexStringLower(SHA256.HashData(package)) };
            transport.Plugins = Json(new[] { entry });
            await library.Catalog.RefreshAsync();
            await library.InstallOfficialAsync(library.Catalog.State.AvailableSources.Single().Plugins.Single(),[]);
            var active = manager.Snapshot().Single().Definition.Metadata;
            var assembly = await File.ReadAllBytesAsync(Path.Combine(code,manifest.EntryAssembly));
            Check(PluginManifestReader.Read(code+".bak").Version == "1.0.0", "C35: existing staged upgrade retains the previous .bak version");
            store.Save(store.Current with { IncludePrereleaseUpdates = true });
            package = await File.ReadAllBytesAsync(PluginInstallationChecks.Zip(root,manifest with { Version = "2.1.0-rc.1" }));
            transport.Plugins = Json(new[] { entry with { Version = "2.1.0-rc.1", Sha256 = Convert.ToHexStringLower(SHA256.HashData(package)) } });
            await library.Catalog.RefreshAsync();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var download = downloads.Send!;
            downloads.Send = async (request, token) => { entered.TrySetResult(); await resume.Task.WaitAsync(token); return await download(request,token); };
            var installing = library.InstallOfficialAsync(library.Catalog.State.AvailableSources.Single().Plugins.Single(),[]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            store.Save(store.Current with { IncludePrereleaseUpdates = false });
            resume.TrySetResult();
            var channelRejected = false;
            try { await installing; } catch (PluginException error) { channelRejected = error.Code == PluginErrorCode.NotListed; }
            Check(channelRejected && ReferenceEquals(active,manager.Snapshot().Single().Definition.Metadata)
                && PluginManifestReader.Read(code).Version == "2.0.0", "C27/C35: switching to stable while downloading cannot commit a prerelease or disturb the installed instance");
            downloads.Send = download;
            transport.Plugins = Json(new[] { entry });
            await library.Catalog.RefreshAsync();
            var cachedIndex = library.Catalog.State.Sources.Single();
            var valid = transport.Plugins;
            foreach (var invalid in new[] { "{}", valid.Replace("\"id\":", "\"id\":\"wini_plugin_bad\",\"id\":"),
                valid.Replace("\"contractVersion\":1", "\"contractVersion\":1.0"),
                Json(new[] { entry with { Permissions = ["invalid-permission"] } }), Json(new[] { entry with { Sha256 = entry.Sha256.ToUpperInvariant() } }),
                Json(new[] { entry with { DownloadUrl = "https://github.com/example/plugins/releases/download/%2f/a.zip" } }),
                Json(new[] { entry with { DownloadUrl = "https://github.com/example/plugins/releases/download/../a.zip" } }) })
            {
                transport.Plugins = invalid;
                await library.Catalog.RefreshAsync();
                Check(library.Catalog.State.Unavailable && ReferenceEquals(cachedIndex,library.Catalog.State.Sources.Single())
                    && ReferenceEquals(active,manager.Snapshot().Single().Definition.Metadata) && !library.Catalog.IsDelisted(active!.External!.Manifest)
                    && manager.Snapshot().Single().StatusKey == "Off" && PluginManifestReader.Read(code+".bak").Version == "1.0.0",
                    "C7/C21-C24/C32: rejected new index leaves installed instance, enablement, backup and old cache untouched");
            }
            foreach (var mismatch in new[] { "hash", "id", "version" })
            {
                var payload = manifest with { Version = "3.0.0" };
                if (mismatch == "id") payload = payload with { Id = "wini_plugin_other" };
                if (mismatch == "version") payload = payload with { Version = "3.0.1" };
                package = await File.ReadAllBytesAsync(PluginInstallationChecks.Zip(root,payload));
                var candidate = entry with { Version = "3.0.0", Sha256 = mismatch == "hash" ? new string('0',64) : Convert.ToHexStringLower(SHA256.HashData(package)) };
                transport.Plugins = Json(new[] { candidate });
                await library.Catalog.RefreshAsync();
                var rejected = false;
                try { await library.InstallOfficialAsync(library.Catalog.State.AvailableSources.Single().Plugins.Single(),[]); }
                catch (PluginException error) { rejected = error.Code == (mismatch == "hash" ? PluginErrorCode.Hash : PluginErrorCode.IndexMismatch); }
                var retainedAssembly = await File.ReadAllBytesAsync(Path.Combine(code,manifest.EntryAssembly));
                Check(rejected && ReferenceEquals(active,manager.Snapshot().Single().Definition.Metadata)
                    && PluginManifestReader.Read(code).Version == "2.0.0" && PluginManifestReader.Read(code+".bak").Version == "1.0.0"
                    && assembly.SequenceEqual(retainedAssembly)
                    && File.ReadAllText(Path.Combine(data,"retain.txt")) == "保留数据"
                    && !Directory.EnumerateDirectories(paths.Slot(PluginSourceKind.Official)).Any(p => Path.GetFileName(p).StartsWith('.'))
                    && !Directory.EnumerateFiles(Path.Combine(paths.Root,".downloads")).Any(),
                    "C35: " + mismatch + " mismatch preserves running instance, files, data and backup without partial installation");
            }
            transport.Plugins = "[]";
            await library.Catalog.RefreshAsync();
            Check(library.Catalog.IsDelisted(active!.External!.Manifest) && manager.Snapshot().Single().StatusKey == "Off"
                && Directory.Exists(code) && !store.IsPluginRemoved(manifest.Id), "C31: delisting warns and excludes updates while preserving disabled installed code");
        }
        else
        {
            manifest = manifest with { Version = "1.1.0" };
            package = await File.ReadAllBytesAsync(PluginInstallationChecks.Zip(root,manifest));
            entry = entry with { Version = manifest.Version, Sha256 = Convert.ToHexStringLower(SHA256.HashData(package)) };
            transport.Plugins = Json(new[] { entry });
            await library.Catalog.RefreshAsync();
            await library.InstallOfficialAsync(library.Catalog.State.AvailableSources.Single().Plugins.Single(),["process"]);
            var trust = manager.Snapshot().Single().Definition.Metadata!.External!.Trust;
            Check(!trust.IsGranted("process") && trust.IsGranted("network") && !trust.IsGranted("native")
                && manager.Snapshot().Single().StatusKey == "Off", "High-risk confirmations cannot expand OS capabilities or auto-enable a third-party package: " + developer);
        }
        await manager.ShutdownAsync();
    }
}
