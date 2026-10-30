using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WindowsIsland.Contracts;
using WindowsIsland.PluginIsolation;

namespace WindowsIsland.Plugins;

internal sealed partial class PluginDesktopBroker
{
    private readonly string? _pluginDirectory;
    private readonly bool _allowBridge;
    private readonly Dictionary<int, BridgeSession> _bridges = [];

    public Task<bool> OpenBridgeAsync(DesktopBridge request, CancellationToken token = default) => RunAsync(() =>
    {
        if (!_allowBridge || _pluginDirectory is null) throw new UnauthorizedAccessException();
        var client = GetClient(request.ProcessId);
        if (request.Library is not { Length: > 4 and <= 80 } || Path.GetFileName(request.Library) != request.Library
            || request.Library.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))
            || !request.Library.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || request.Sha256 is not { Length: 64 } || !request.Sha256.All(Uri.IsHexDigit)
            || request.TargetSha256 is not { Length: 64 } || !request.TargetSha256.All(Uri.IsHexDigit)) throw new InvalidDataException();
        if (_bridges.TryGetValue(request.ProcessId, out var existing)) return existing.Active;
        var library = Path.Combine(_pluginDirectory, request.Library);
        var session = new BridgeSession(request.ProcessId, library);
        try
        {
            NativeBridgeInjector.Attach(client.Process.Id, library, session.PipeName,
                request.Sha256, request.TargetSha256, request.Magic, request.Version, expectedStartedAt: client.StartedAt);
            session.Attached = true;
            _bridges.Add(request.ProcessId, session);
            return true;
        }
        catch (NotSupportedException) { session.Dispose(); return false; }
        catch { session.Dispose(); throw; }
    }, token);

    public Task<DesktopBridgeData> ReadBridgeAsync(int processId, CancellationToken token = default) => RunAsync(() =>
    {
        GetClient(processId);
        return _bridges.TryGetValue(processId, out var session) ? session.Drain() : new DesktopBridgeData(false, []);
    }, token);

    public Task<bool> CloseBridgeAsync(int processId, CancellationToken token = default) => RunAsync(() =>
    {
        GetClient(processId);
        CloseBridge(processId);
        return true;
    }, token);

    private void CloseBridge(int processId)
    {
        if (_bridges.Remove(processId, out var session)) session.Dispose();
    }

    private sealed class BridgeSession : IDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<byte[]> _bytes = new();
        private readonly Task _reader;
        private int _buffered, _disposed;
        private readonly int _processId;
        private readonly string _library;
        public bool Attached;
        public volatile bool Active = true;
        public string PipeName { get; } = $"WindowsIsland.Bridge.{Environment.ProcessId}.{Guid.NewGuid():N}";

        public BridgeSession(int processId, string library)
        {
            _processId = processId; _library = library;
            _pipe = new(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 16384, 16384);
            _reader = ReadAsync(processId);
        }

        private async Task ReadAsync(int processId)
        {
            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                handshake.CancelAfter(TimeSpan.FromSeconds(10));
                await _pipe.WaitForConnectionAsync(handshake.Token).ConfigureAwait(false);
                if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var pid) || pid != processId) throw new InvalidDataException();
                var buffer = new byte[16384];
                while (!_stop.IsCancellationRequested)
                {
                    var count = await _pipe.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    if (Interlocked.Add(ref _buffered, count) > 1024 * 1024) throw new InvalidDataException();
                    _bytes.Enqueue(buffer.AsSpan(0, count).ToArray());
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            finally { Active = false; _pipe.Dispose(); }
        }

        public DesktopBridgeData Drain()
        {
            using var stream = new MemoryStream();
            while (_bytes.TryPeek(out var next) && stream.Length + next.Length <= 1024 * 1024 && _bytes.TryDequeue(out var bytes))
            {
                Interlocked.Add(ref _buffered, -bytes.Length);
                stream.Write(bytes);
            }
            return new(Active, stream.ToArray());
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Active = false;
            _stop.Cancel(); _pipe.Dispose();
            _ = _reader.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
            if (!Attached) return;
            // Give the native sender time to observe EOF and unload before package replacement.
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(2))
            {
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById(_processId);
                    if (!process.Modules.Cast<System.Diagnostics.ProcessModule>().Any(module =>
                        string.Equals(module.FileName, _library, StringComparison.OrdinalIgnoreCase))) break;
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { break; }
                Thread.Sleep(25);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    }
}
