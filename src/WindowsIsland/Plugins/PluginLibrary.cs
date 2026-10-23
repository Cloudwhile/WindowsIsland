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
    public PluginLibrary(PluginManager manager, PluginPaths paths, PluginCatalogClient? client = null, PluginCatalogClient? downloads = null,
        SettingsStore? settings = null)
    {
        Manager = manager;
        Paths = paths;
        Installer = new(paths);
        Catalog = new(paths, client, () => settings?.Current.IncludePrereleaseUpdates ?? false);
        _downloads = downloads ?? new PluginCatalogClient();
    }

    public static bool Compatible(PluginCatalogEntry entry) => PluginContract.IsCompatible(entry.ContractVersion)
        && CatalogVersion.Parse(ReleaseVersion.Current.Text).CompareTo(CatalogVersion.Parse(entry.MinHostVersion)) >= 0;

    public static string? InstallUnavailableReason(PluginCatalogEntry entry, PluginMetadata? installed, bool includePrereleases = false)
    {
        if (!includePrereleases && CatalogVersion.Parse(entry.Version).IsPrerelease) return "";
        if (installed is not null && CatalogVersion.Parse(entry.Version).CompareTo(CatalogVersion.Parse(installed.Version)) < 0) return "";
        if (installed?.External is { } external)
        {
            if (external.Manifest.SourceKind != PluginSourceKind.Official) return "PluginInstalledElsewhere";
            if (external.Manifest.DeveloperId != entry.DeveloperId) return "PluginDeveloperMismatch";
        }
        if (!PluginContract.IsCompatible(entry.ContractVersion)) return "PluginContractIncompatible";
        if (CatalogVersion.Parse(ReleaseVersion.Current.Text).CompareTo(CatalogVersion.Parse(entry.MinHostVersion)) < 0)
            return "PluginHostTooOld";
        return installed is not null && CatalogVersion.Parse(entry.Version)
            .CompareTo(CatalogVersion.Parse(installed.Version)) == 0 ? "PluginAlreadyLatest" : null;
    }

    public async Task InstallOfficialAsync(PluginCatalogEntry entry, IEnumerable<string> confirmed, CancellationToken token = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        operation.CancelAfter(TimeSpan.FromMinutes(5));
        var cancellation = operation.Token;
        var index = Catalog.State.AvailableSources.SingleOrDefault(s => s.Source.DeveloperId == entry.DeveloperId
            && s.Plugins.Contains(entry)) ?? throw new PluginException(PluginErrorCode.NotListed, "插件已不在当前官方索引中。");
        if (!Compatible(entry)) throw new PluginException(PluginErrorCode.Contract, "插件合约版本与消息岛不兼容。");
        if (!Catalog.ChannelAllows(entry)) throw new PluginException(PluginErrorCode.NotListed, "当前更新渠道不提供此版本。", "C27");
        var file = Path.Combine(Paths.Root, ".downloads", Guid.NewGuid().ToString("N") + ".zip");
        PluginPaths.CheckLinks(file);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            await _downloads.DownloadAsync(entry, file, cancellation, Catalog.State.GitHubProxy);
            // 官方身份仅由已经校验的索引授予，包中自称官方不会改变来源类型或权限。
            var grants = entry.DeveloperId == PluginCatalogClient.OfficialDeveloperId
                ? entry.Permissions.Where(PluginPermissions.IsHighRisk) : confirmed;
            using var prepared = await Installer.PrepareAsync(file, PluginSourceKind.Official, grants, cancellation);
            var manifest = prepared.Manifest;
            if (manifest.Id != entry.Id || manifest.DeveloperId != entry.DeveloperId || manifest.Version != entry.Version
                || manifest.RepositoryUrl != entry.RepositoryUrl || !manifest.Permissions.Order().SequenceEqual(entry.Permissions.Order()))
                throw new PluginException(PluginErrorCode.IndexMismatch, "插件包与官方索引不一致。", "C35");
            if (!Catalog.ChannelAllows(entry) || !Catalog.State.AvailableSources.Any(s => s.Source == index.Source && s.Plugins.Contains(entry)))
                throw new PluginException(PluginErrorCode.NotListed, "插件已不在当前官方索引中。");
            await Manager.InstallAsync(prepared, token: cancellation);
        }
        catch (PluginException error) when (error.Code is PluginErrorCode.Hash or PluginErrorCode.IndexMismatch)
        {
            Diagnostics.Capture(DiagnosticArea.Application, error, "Catalog.C35");
            throw;
        }
        finally { File.Delete(file); }
    }

    public void Dispose() { _lifetime.Cancel(); Catalog.Dispose(); _downloads.Dispose(); }
}
