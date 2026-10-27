using System.Text.Json;
using System.Threading.Channels;
using WindowsIsland.Contracts;
using WindowsIsland.PluginIsolation;

namespace WindowsIsland.PluginWorker;

internal sealed class WorkerContext : IPluginContext, IPluginSettings, IPluginDiagnostics,
    IPluginStorage, IPluginPermissions, IPinnedContentSource, IPluginNetwork, IDisposable
{
    private sealed record Outbound(string Method, object Payload);
    private readonly IsolationRpc _rpc;
    private readonly CancellationTokenSource _lifetime;
    private readonly HashSet<string> _granted;
    private readonly Channel<Outbound> _outbound = Channel.CreateBounded<Outbound>(new BoundedChannelOptions(64)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private Func<PinnedControlInteraction, Task>? _interaction;
    private int _revoked;

    public string PluginId { get; }
    public string HostVersion { get; }
    public string? PreviousVersion { get; }
    public string DataDirectory { get; }
    public IPluginSettings Settings => this;
    public IPluginDiagnostics Diagnostics => this;

    public WorkerContext(IsolationRpc rpc, WorkerInitialization initialization, CancellationToken lifetime)
    {
        _rpc = rpc;
        PluginId = initialization.Manifest.Id;
        HostVersion = initialization.HostVersion;
        PreviousVersion = initialization.PreviousVersion;
        DataDirectory = Path.GetFullPath(initialization.DataDirectory);
        _granted = new(initialization.Granted, StringComparer.Ordinal);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _ = Task.Run(PumpAsync);
    }

    public T? GetService<T>() where T : class
    {
        if (Volatile.Read(ref _revoked) != 0) return null;
        var type = typeof(T);
        if (type == typeof(IPluginPermissions) || type == typeof(IPinnedContentSource)) return this as T;
        if (type == typeof(IPluginStorage) && IsGranted(PluginPermissions.Settings)) return this as T;
        if (type == typeof(IWeChatHost) && IsGranted(PluginPermissions.WeChat)) return new WorkerWeChatHost(_rpc, _lifetime.Token) as T;
        if (type == typeof(IPluginNetwork) && IsGranted(PluginPermissions.Network)) return this as T;
        return null;
    }

    public bool IsGranted(string permission) => Volatile.Read(ref _revoked) == 0 && _granted.Contains(permission);

    public string? GetString(string key, string? defaultValue = null)
    {
        ValidateKey(key);
        return Volatile.Read(ref _revoked) == 0
            ? Call<string>("getString", new WorkerSetting(key, defaultValue)) ?? defaultValue : defaultValue;
    }

    public bool GetBoolean(string key, bool defaultValue = false)
    {
        ValidateKey(key);
        return Volatile.Read(ref _revoked) == 0
            ? Call<bool>("getBoolean", new WorkerSetting(key, Boolean: defaultValue)) : defaultValue;
    }

    public void SetString(string key, string? value)
    {
        ValidateKey(key);
        if (value?.Length > 32768) throw new ArgumentException("Invalid plugin setting value.", nameof(value));
        if (Volatile.Read(ref _revoked) == 0) Call<JsonElement>("setString", new WorkerSetting(key, value));
    }

    public void SetBoolean(string key, bool value)
    {
        ValidateKey(key);
        if (Volatile.Read(ref _revoked) == 0) Call<JsonElement>("setBoolean", new WorkerSetting(key, Boolean: value));
    }

    public void Write(PluginDiagnostic diagnostic) => Enqueue("diagnostic", diagnostic);
    public void PublishNotification(PluginNotification notification) => Enqueue("notification", notification);
    public void Submit(PinnedContent content) => Enqueue("pinned", new WorkerPinnedChange("Added", content.Id, content));
    public void Update(PinnedContent content) => Enqueue("pinned", new WorkerPinnedChange("Updated", content.Id, content));
    public void Remove(string id) => Enqueue("pinned", new WorkerPinnedChange("Removed", id));

    public void SetInteractionHandler(Func<PinnedControlInteraction, Task>? handler)
    {
        if (Volatile.Read(ref _revoked) == 0) Volatile.Write(ref _interaction, handler);
        if (Volatile.Read(ref _revoked) != 0) Interlocked.Exchange(ref _interaction, null);
    }

    public Task InteractAsync(PinnedControlInteraction interaction) => Volatile.Read(ref _revoked) == 0
        ? Volatile.Read(ref _interaction)?.Invoke(interaction) ?? Task.CompletedTask : Task.CompletedTask;

    public async Task<PluginNetworkResponse> SendAsync(PluginNetworkRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsGranted(PluginPermissions.Network)) throw new UnauthorizedAccessException("Network access is unavailable.");
        ArgumentNullException.ThrowIfNull(request);
        if (request.Body?.Length > PluginNetworkLimits.MaximumRequestBytes
            || request.Url is null || request.Url.Length > PluginNetworkLimits.MaximumUrlLength
            || request.Purpose is null || request.Purpose.Length > PluginNetworkLimits.MaximumPurposeLength
            || request.Headers?.Count > PluginNetworkLimits.MaximumHeaders)
            throw new ArgumentException("Invalid network request.", nameof(request));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        return await _rpc.CallAsync<PluginNetworkResponse>("network", request, timeout.Token).ConfigureAwait(false)
            ?? throw new IOException("Network operation was rejected.");
    }

    private T? Call<T>(string method, object payload)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        return _rpc.CallAsync<T>(method, payload, timeout.Token).GetAwaiter().GetResult();
    }

    private void Enqueue(string method, object payload)
    {
        if (Volatile.Read(ref _revoked) != 0) return;
        if (!_outbound.Writer.TryWrite(new(method, payload)))
            throw new InvalidOperationException("Plugin callback queue is full.");
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var item in _outbound.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (Volatile.Read(ref _revoked) != 0) return;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                try { await _rpc.CallAsync<JsonElement>(item.Method, item.Payload, timeout.Token).ConfigureAwait(false); }
                catch (Exception) when (!_lifetime.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
            throw new ArgumentException("Invalid plugin setting key.", nameof(key));
    }

    public void Revoke()
    {
        if (Interlocked.Exchange(ref _revoked, 1) != 0) return;
        Interlocked.Exchange(ref _interaction, null);
        _outbound.Writer.TryComplete();
        _lifetime.Cancel();
    }

    public void Dispose() => Revoke();
}
