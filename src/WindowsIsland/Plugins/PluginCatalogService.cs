using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WindowsIsland.Contracts;
using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

internal sealed record PluginCatalogState(string BaseUrl, PluginSourceIndex[] Sources, bool Busy = false, bool Unavailable = false,
    bool Fresh = false, DateTimeOffset? LastSync = null, string GitHubProxy = "", bool Disabled = false,
    PluginErrorCode? Failure = null, string[]? Listed = null, string[]? Known = null, string[]? FreshDevelopers = null)
{
    [JsonIgnore] public PluginSourceIndex[] AvailableSources => Disabled ? [] : Sources.Where(s =>
        Listed is null || Listed.Contains(s.Source.DeveloperId, StringComparer.Ordinal)).ToArray();
}

internal sealed class PluginCatalogService : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _persistence = new(1, 1);
    private readonly PluginCatalogClient _client;
    private readonly string _cache;
    private readonly Func<bool> _includePrereleases;
    private readonly CancellationTokenSource _lifetime = new();
    private Task _running = Task.CompletedTask;
    private PluginCatalogState _state = new(PluginCatalogClient.DefaultBaseUrl, []);
    private bool _disposed, _attempted;
    private int _revision;
    public event Action? Changed;
    public PluginCatalogState State { get { lock (_gate) return _state; } }
    public bool IncludePrereleases => _includePrereleases();
    public PluginCatalogService(PluginPaths paths, PluginCatalogClient? client = null, Func<bool>? includePrereleases = null)
    {
        _client = client ?? new PluginCatalogClient();
        _cache = Path.Combine(paths.Root, "sources.json");
        _includePrereleases = includePrereleases ?? (() => false);
    }

    public async Task LoadAsync()
    {
        await _persistence.WaitAsync(_lifetime.Token);
        try
        {
            PluginPaths.CheckLinks(_cache);
            if (!File.Exists(_cache)) return;
            var json = JsonNode.Parse(await File.ReadAllTextAsync(_cache, _lifetime.Token))?.AsObject();
            if (json is null) throw new JsonException();
            var address = json["baseUrl"] is JsonValue b && b.TryGetValue<string>(out var raw) ? raw : "";
            var proxy = json["gitHubProxy"] is JsonValue p && p.TryGetValue<string>(out var text) ? text : "";
            try { proxy = GitHubDownloadProxy.Normalize(proxy); } catch (PluginException) { proxy = ""; }
            try { address = PluginCatalogPolicy.BaseAddress(address); }
            catch (PluginException error)
            {
                lock (_gate) if (!_disposed && !_attempted)
                    _state = new(address, [], Unavailable: true, GitHubProxy: proxy, Disabled: true, Failure: error.Code);
                Record(error);
                return;
            }
            // Preserve configuration even if the local index cache is damaged.
            lock (_gate) if (!_disposed && !_attempted) _state = new(address, [], GitHubProxy: proxy);
            json["baseUrl"] = address;
            json["gitHubProxy"] = proxy;
            var cached = json.Deserialize<PluginCatalogState>(PluginManifestReader.Json) ?? throw new JsonException();
            var developers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in cached.Sources)
            {
                PluginCatalogClient.ValidateSource(source.Source, new Uri(address));
                if (!developers.Add(source.Source.DeveloperId) || source.Plugins.Length > 2048) throw new JsonException();
                foreach (var plugin in source.Plugins) PluginCatalogClient.ValidateEntry(plugin, source.Source);
            }
            PluginCatalogPolicy.Directory(cached.AvailableSources);
            lock (_gate) if (!_disposed && !_attempted) _state = cached with
            {
                Busy = false, Fresh = false, FreshDevelopers = [], Unavailable = false, Disabled = false, Failure = null,
                Known = cached.Known ?? developers.ToArray(), Listed = cached.Listed ?? developers.ToArray()
            };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { Record(new(PluginErrorCode.CatalogCache, "目录缓存读取失败。", "C32")); }
        finally { _persistence.Release(); Changed?.Invoke(); }
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
            if (_disposed || !_running.IsCompleted) return _running;
            _attempted = true;
            var address = _state.BaseUrl;
            var revision = _revision;
            _state = _state with { Busy = true, Fresh = false, FreshDevelopers = [] };
            _running = Task.Run(() => RunAsync(address, revision, token));
            return _running;
        }
    }

    public Task ConfigureProxyAsync(string? proxy) => ConfigureAsync(null, GitHubDownloadProxy.Normalize(proxy));
    public Task ConfigureBaseAsync(string? address) => ConfigureAsync(PluginCatalogPolicy.BaseAddress(address), null);
    private async Task ConfigureAsync(string? address, string? proxy)
    {
        await _persistence.WaitAsync(_lifetime.Token);
        try
        {
            var previous = State;
            var changed = address is not null && (previous.BaseUrl != address || previous.Disabled);
            var configured = changed ? new PluginCatalogState(address!, [], Busy: previous.Busy, GitHubProxy: previous.GitHubProxy) : previous;
            if (proxy is not null) configured = configured with { GitHubProxy = proxy };
            await PersistAsync(configured, _lifetime.Token);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (changed) _revision++;
                _state = configured;
            }
        }
        finally { _persistence.Release(); }
        Changed?.Invoke();
    }

    private async Task RunAsync(string address, int revision, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, token);
        try
        {
            _ = PluginCatalogPolicy.BaseAddress(address);
            var fetched = await _client.FetchAsync(address, cancellation.Token);
            await _persistence.WaitAsync(cancellation.Token);
            try
            {
                if (revision != _revision) return; // C1/C4: the old flight cannot repopulate a changed base.
                var previous = State;
                var replacements = fetched.Indices.ToDictionary(s => s.Source.DeveloperId, StringComparer.Ordinal);
                var merged = replacements.Count == 0 ? previous.Sources
                    : previous.Sources.Where(s => !replacements.ContainsKey(s.Source.DeveloperId)).Concat(fetched.Indices).ToArray();
                var listed = fetched.Listed.Select(s => s.DeveloperId).ToArray();
                var failures = fetched.Failures.ToList();
                var accepted = fetched.Indices;
                try { PluginCatalogPolicy.Directory(merged.Where(s => listed.Contains(s.Source.DeveloperId, StringComparer.Ordinal))); }
                catch (PluginException error)
                {
                    failures.Add(error);
                    merged = previous.Sources;
                    accepted = [];
                }
                var result = previous with
                {
                    Sources = merged, Busy = false, Disabled = false, Unavailable = failures.Count != 0,
                    Failure = failures.FirstOrDefault()?.Code, Fresh = true,
                    FreshDevelopers = accepted.Select(s => s.Source.DeveloperId).ToArray(), Listed = listed,
                    Known = (previous.Known ?? []).Concat(listed).Distinct(StringComparer.Ordinal).ToArray(), LastSync = DateTimeOffset.UtcNow
                };
                await PersistAsync(result, cancellation.Token);
                lock (_gate) if (!_disposed) _state = result;
                foreach (var failure in failures) Record(failure);
            }
            finally { _persistence.Release(); }
        }
        catch (Exception error)
        {
            var failure = error as PluginException ?? new PluginException(PluginErrorCode.CatalogFetch, "目录刷新失败。", "C10");
            lock (_gate) if (!_disposed && revision == _revision)
                _state = _state with { Busy = false, Unavailable = true, Fresh = false, FreshDevelopers = [],
                    Disabled = failure.Code == PluginErrorCode.SourceAddress, Failure = failure.Code };
            if (!cancellation.IsCancellationRequested) Record(failure);
        }
        finally
        {
            lock (_gate) if (!_disposed) _state = _state with { Busy = false };
            if (!_disposed) Changed?.Invoke();
        }
    }

    private async Task PersistAsync(PluginCatalogState state, CancellationToken token)
    {
        PluginPaths.CheckLinks(_cache);
        Directory.CreateDirectory(Path.GetDirectoryName(_cache)!);
        var temporary = _cache + ".tmp";
        PluginPaths.CheckLinks(temporary);
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                await JsonSerializer.SerializeAsync(stream, state with { Fresh = false, FreshDevelopers = [], Busy = false }, PluginManifestReader.Json, token);
            File.Move(temporary, _cache, true);
        }
        finally { File.Delete(temporary); }
    }

    private static void Record(PluginException error) => Diagnostics.Capture(DiagnosticArea.Application, error, "Catalog." + (error.Constraint ?? "C34"));

    public bool IsDelisted(PluginManifest manifest)
    {
        var state = State;
        if (manifest.SourceKind != PluginSourceKind.Official || state.Disabled) return false;
        return state.Fresh && (state.Known ?? []).Contains(manifest.DeveloperId, StringComparer.Ordinal)
                && !(state.Listed ?? []).Contains(manifest.DeveloperId, StringComparer.Ordinal)
            || (state.FreshDevelopers ?? []).Contains(manifest.DeveloperId, StringComparer.Ordinal)
                && !state.Sources.Any(s => s.Source.DeveloperId == manifest.DeveloperId && s.Plugins.Any(p => p.Id == manifest.Id));
    }

    public PluginCatalogEntry? UpdateFor(PluginManifest manifest)
    {
        if (manifest.SourceKind != PluginSourceKind.Official || IsDelisted(manifest)) return null;
        return State.AvailableSources.Where(s => s.Source.DeveloperId == manifest.DeveloperId).SelectMany(s => s.Plugins)
            .FirstOrDefault(p => p.Id == manifest.Id && PluginLibrary.Compatible(p) && ChannelAllows(p)
                && CatalogVersion.Parse(p.Version).CompareTo(CatalogVersion.Parse(manifest.Version)) > 0);
    }
    public bool ChannelAllows(PluginCatalogEntry entry) => IncludePrereleases || !CatalogVersion.Parse(entry.Version).IsPrerelease;

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        _lifetime.Cancel();
        _client.Dispose();
        Changed = null;
    }
}
