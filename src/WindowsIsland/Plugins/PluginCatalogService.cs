using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

internal sealed record PluginCatalogState(string BaseUrl, PluginSourceIndex[] Sources, bool Busy = false, bool Unavailable = false,
    bool Fresh = false, DateTimeOffset? LastSync = null);

internal sealed class PluginCatalogService : IDisposable
{
    private readonly object _gate = new();
    private readonly PluginCatalogClient _client;
    private readonly string _cache;
    private readonly CancellationTokenSource _lifetime = new();
    private Task _running = Task.CompletedTask;
    private PluginCatalogState _state = new(PluginCatalogClient.DefaultBaseUrl, []);
    private bool _disposed, _attempted;
    public event Action? Changed;
    public PluginCatalogState State { get { lock (_gate) return _state; } }
    public PluginCatalogService(PluginPaths paths, PluginCatalogClient? client = null)
    {
        _client = client ?? new PluginCatalogClient();
        _cache = Path.Combine(paths.Root, "sources.json");
    }

    public async Task LoadAsync()
    {
        try
        {
            var cached = await Task.Run(() =>
            {
                PluginPaths.CheckLinks(_cache);
                if (!File.Exists(_cache)) return null;
                if (new FileInfo(_cache).Length > 4 * 1024 * 1024) throw new PluginException(PluginErrorCode.CacheSize, "官方源缓存过大。");
                return JsonSerializer.Deserialize<PluginCatalogState>(File.ReadAllText(_cache), PluginManifestReader.Json);
            });
            if (cached is null) return;
            var baseUri = PluginCatalogClient.BaseUri(cached.BaseUrl);
            var ids = new HashSet<string>();
            foreach (var source in cached.Sources)
            {
                PluginCatalogClient.ValidateSource(source.Source, baseUri);
                foreach (var plugin in source.Plugins) { PluginCatalogClient.ValidateEntry(plugin, source.Source); if (!ids.Add(plugin.Id)) throw new PluginException(PluginErrorCode.IndexDuplicate, "官方插件标识重复。"); }
            }
            lock (_gate) if (!_disposed && !_attempted) _state = cached with { Busy = false, Fresh = false, Unavailable = false };
            Changed?.Invoke();
        }
        catch (Exception error) { Diagnostics.Capture(DiagnosticArea.Application, error, "ReadPluginSources"); }
    }

    public Task StartOnce()
    {
        lock (_gate) { if (_attempted || _disposed) return _running; }
        return RefreshAsync();
    }

    public Task RefreshAsync(CancellationToken token = default)
    {
        lock (_gate)
        {
            if (_disposed || _state.Busy) return _running;
            _attempted = true;
            _state = _state with { Busy = true };
            _running = Task.Run(() => RunAsync(token));
            return _running;
        }
    }

    public async Task ConfigureAsync(string baseUrl)
    {
        var url = PluginCatalogClient.BaseUri(baseUrl).AbsoluteUri;
        // 旧请求完成后才切源，避免旧索引覆盖新地址。
        Task pending;
        lock (_gate) pending = _running;
        await pending;
        var configured = new PluginCatalogState(url, []);
        await PersistAsync(configured, _lifetime.Token);
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _state = configured; _attempted = false; }
        Changed?.Invoke();
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var source = State.BaseUrl;
            var indices = await _client.FetchAsync(source, timeout.Token);
            var result = new PluginCatalogState(source, indices, Fresh: true, LastSync: DateTimeOffset.UtcNow);
            await PersistAsync(result, timeout.Token);
            lock (_gate) if (!_disposed) _state = result;
        }
        catch (Exception error)
        {
            if (!_lifetime.IsCancellationRequested && error is not OperationCanceledException) Diagnostics.Capture(DiagnosticArea.Application, error, "RefreshPluginSources");
            lock (_gate) if (!_disposed) _state = _state with { Busy = false, Unavailable = true };
        }
        if (!_disposed) Changed?.Invoke();
    }

    private async Task PersistAsync(PluginCatalogState state, CancellationToken token)
    {
        PluginPaths.CheckLinks(_cache);
        Directory.CreateDirectory(Path.GetDirectoryName(_cache)!);
        var temporary = _cache + ".tmp";
        PluginPaths.CheckLinks(temporary);
        var content = JsonSerializer.SerializeToUtf8Bytes(state, PluginManifestReader.Json);
        if (content.Length > 4 * 1024 * 1024) throw new PluginException(PluginErrorCode.CacheSize, "官方源缓存过大。");
        try { await File.WriteAllBytesAsync(temporary, content, token); File.Move(temporary, _cache, true); }
        finally { File.Delete(temporary); }
    }

    public bool IsDelisted(PluginManifest manifest)
    {
        var state = State;
        return manifest.SourceKind == PluginSourceKind.Official && state.Fresh
            && !state.Sources.Any(s => s.Source.DeveloperId == manifest.DeveloperId && s.Plugins.Any(p => p.Id == manifest.Id));
    }

    public PluginCatalogEntry? UpdateFor(PluginManifest manifest)
    {
        var state = State;
        if (manifest.SourceKind != PluginSourceKind.Official || IsDelisted(manifest)) return null;
        return state.Sources.Where(s => s.Source.DeveloperId == manifest.DeveloperId).SelectMany(s => s.Plugins)
            .FirstOrDefault(p => p.Id == manifest.Id && ReleaseVersion.FromInformationalVersion(p.Version).CompareTo(ReleaseVersion.FromInformationalVersion(manifest.Version)) > 0);
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        _lifetime.Cancel();
        _client.Dispose();
        Changed = null;
    }
}
