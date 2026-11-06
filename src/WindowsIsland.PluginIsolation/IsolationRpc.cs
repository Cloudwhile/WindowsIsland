using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;
using WindowsIsland.Contracts;

namespace WindowsIsland.PluginIsolation;

// Private inherited pipe endpoints identify the session. Neither peer supplies a caller identity.
public sealed class IsolationRpc : IDisposable
{
    public const int MaxFrame = 4 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, MaxDepth = 32 };
    private sealed record Packet(long Id, string? Method, JsonElement Payload, bool Failed = false, bool Cancelled = false);
    private readonly Stream _input, _output;
    private readonly Func<string, JsonElement, CancellationToken, Task<object?>> _handler;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _requests = new();
    private long _sequence;
    private int _inflight, _disposed;
    public Task Completion { get; }

    public IsolationRpc(Stream input, Stream output, Func<string, JsonElement, CancellationToken, Task<object?>> handler)
    {
        _input = input; _output = output; _handler = handler;
        Completion = Task.Run(ReadLoopAsync);
    }

    public async Task<T?> CallAsync<T>(string method, object? payload = null, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (string.IsNullOrEmpty(method) || method.Length > 48 || _pending.Count >= 32)
            throw new InvalidDataException("Invalid sandbox request.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var id = Interlocked.Increment(ref _sequence);
        var result = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, result)) throw new InvalidOperationException();
        try
        {
            await WriteAsync(new(id, method, JsonSerializer.SerializeToElement(payload, Json)), timeout.Token);
            var data = await result.Task.WaitAsync(timeout.Token);
            return data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? default : data.Deserialize<T>(Json);
        }
        catch (OperationCanceledException)
        {
            using var cancelWrite = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            cancelWrite.CancelAfter(TimeSpan.FromMilliseconds(500));
            try { await WriteAsync(new(id, null, JsonSerializer.SerializeToElement<object?>(null), Cancelled: true), cancelWrite.Token); }
            catch { Dispose(); }
            throw;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task WriteAsync(Packet packet, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(packet, Json);
        if (bytes.Length > MaxFrame) throw new InvalidDataException("Sandbox message exceeds the limit.");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await _write.WaitAsync(token);
        try { await _output.WriteAsync(prefix, token); await _output.WriteAsync(bytes, token); await _output.FlushAsync(token); }
        finally { _write.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var prefix = new byte[4];
                await _input.ReadExactlyAsync(prefix, _lifetime.Token);
                var size = BinaryPrimitives.ReadInt32LittleEndian(prefix);
                if (size is <= 0 or > MaxFrame) throw new InvalidDataException();
                var bytes = new byte[size];
                await _input.ReadExactlyAsync(bytes, _lifetime.Token);
                var packet = JsonSerializer.Deserialize<Packet>(bytes, Json) ?? throw new InvalidDataException();
                if (packet.Id <= 0) throw new InvalidDataException();
                if (packet.Method is null)
                {
                    if (packet.Cancelled)
                    {
                        if (_requests.TryGetValue(packet.Id, out var cancellation)) cancellation.Cancel();
                        continue;
                    }
                    if (!_pending.TryGetValue(packet.Id, out var result)) continue; // A cancelled request may finish late.
                    if (packet.Failed) result.TrySetException(new InvalidOperationException("Sandbox operation was rejected."));
                    else result.TrySetResult(packet.Payload);
                    continue;
                }
                if (packet.Method.Length is 0 or > 48 || Interlocked.Increment(ref _inflight) > 16)
                    throw new InvalidDataException();
                var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                if (!_requests.TryAdd(packet.Id, requestLifetime)) { requestLifetime.Dispose(); throw new InvalidDataException(); }
                _ = Task.Run(() => HandleAsync(packet, requestLifetime));
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or JsonException or InvalidDataException or ObjectDisposedException)
        { }
        finally { Dispose(); }
    }

    private async Task HandleAsync(Packet packet, CancellationTokenSource requestLifetime)
    {
        try
        {
            object? value = null;
            var failed = false;
            try { value = await _handler(packet.Method!, packet.Payload, requestLifetime.Token); }
            catch { failed = true; }
            await WriteAsync(new(packet.Id, null, JsonSerializer.SerializeToElement(value, Json), failed), _lifetime.Token);
        }
        catch { Dispose(); }
        finally { _requests.TryRemove(packet.Id, out _); requestLifetime.Dispose(); Interlocked.Decrement(ref _inflight); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _lifetime.Cancel(); }
        catch (AggregateException) { }
        finally
        {
            foreach (var item in _pending.Values) item.TrySetException(new IOException("Sandbox connection closed."));
            try { _input.Dispose(); }
            finally { _output.Dispose(); }
        }
    }
}

public sealed record WorkerInitialization(PluginManifest Manifest, string DataDirectory, string[] Granted, string? PreviousVersion,
    string HostVersion = "unknown", string CodeDirectory = "");
public sealed record WorkerDescription(PluginStatus Status, PluginSetting[] Settings, PluginConversationRule[]? ConversationRules = null,
    long Revision = 0);
public sealed record WorkerDesktopRequest(string Operation, JsonElement Payload);
public sealed record DesktopWindowRequest(int ProcessId, long Window);
public sealed record WorkerSetting(string Key, string? Value = null, bool Boolean = false);
public sealed record WorkerPinnedChange(string Kind, string Id, PinnedContent? Content = null);
