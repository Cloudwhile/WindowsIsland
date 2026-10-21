using WindowsIsland.Contracts;
using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

internal sealed class PluginLibrary : IDisposable
{
    private readonly PluginCatalogClient _downloads;
    private readonly CancellationTokenSource _lifetime = new();
    public PluginManager Manager { get; }
    public PluginPaths Paths { get; }
    public PluginInstaller Installer { get; }
    public PluginCatalogService Catalog { get; }
    public PluginLibrary(PluginManager manager, PluginPaths paths, PluginCatalogClient? client = null, PluginCatalogClient? downloads = null)
    {
        Manager = manager;
        Paths = paths;
        Installer = new(paths);
        Catalog = new(paths, client);
        _downloads = downloads ?? new PluginCatalogClient();
    }

    public static bool Compatible(PluginCatalogEntry entry) => PluginContract.IsCompatible(entry.ContractVersion)
        && ReleaseVersion.Current.CompareTo(ReleaseVersion.FromInformationalVersion(entry.MinHostVersion)) >= 0;

    public static string? InstallUnavailableReason(PluginCatalogEntry entry, PluginMetadata? installed)
    {
        if (installed?.External is { } external)
        {
            if (external.Manifest.SourceKind != PluginSourceKind.Official) return "PluginInstalledElsewhere";
            if (external.Manifest.DeveloperId != entry.DeveloperId) return "PluginDeveloperMismatch";
        }
        if (!PluginContract.IsCompatible(entry.ContractVersion)) return "PluginContractIncompatible";
        if (ReleaseVersion.Current.CompareTo(ReleaseVersion.FromInformationalVersion(entry.MinHostVersion)) < 0)
            return "PluginHostTooOld";
        return installed is not null && ReleaseVersion.FromInformationalVersion(entry.Version)
            .CompareTo(ReleaseVersion.FromInformationalVersion(installed.Version)) <= 0 ? "PluginAlreadyLatest" : null;
    }

    public async Task InstallOfficialAsync(PluginCatalogEntry entry, IEnumerable<string> confirmed, CancellationToken token = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        operation.CancelAfter(TimeSpan.FromMinutes(5));
        var cancellation = operation.Token;
        var index = Catalog.State.Sources.SingleOrDefault(s => s.Source.DeveloperId == entry.DeveloperId
            && s.Plugins.Contains(entry)) ?? throw new InvalidDataException("插件已不在当前官方索引中。");
        if (!Compatible(entry)) throw new InvalidDataException("插件合约版本与消息岛不兼容。");
        var file = Path.Combine(Paths.Root, ".downloads", Guid.NewGuid().ToString("N") + ".zip");
        PluginPaths.CheckLinks(file);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            await _downloads.DownloadAsync(entry, file, cancellation);
            // 官方身份仅由已经校验的索引授予，包中自称官方不会改变来源类型或权限。
            var grants = entry.DeveloperId == PluginCatalogClient.OfficialDeveloperId
                ? entry.Permissions.Where(PluginPermissions.IsHighRisk) : confirmed;
            using var prepared = await Installer.PrepareAsync(file, PluginSourceKind.Official, grants, cancellation);
            var manifest = prepared.Manifest;
            if (manifest.Id != entry.Id || manifest.DeveloperId != entry.DeveloperId || manifest.Version != entry.Version
                || manifest.RepositoryUrl != entry.RepositoryUrl || !manifest.Permissions.Order().SequenceEqual(entry.Permissions.Order()))
                throw new InvalidDataException("插件包与官方索引不一致。");
            if (!Catalog.State.Sources.Any(s => s.Source == index.Source && s.Plugins.Contains(entry)))
                throw new InvalidDataException("插件已不在当前官方索引中。");
            await Manager.InstallAsync(prepared, token: cancellation);
        }
        finally { File.Delete(file); }
    }

    public void Dispose() { _lifetime.Cancel(); Catalog.Dispose(); _downloads.Dispose(); }
}
