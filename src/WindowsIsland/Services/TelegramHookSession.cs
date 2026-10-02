using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WindowsIsland.Services;

internal sealed class TelegramHookSession : IDisposable
{
    private readonly int _processId;
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<IslandNotification> _receive;
    private readonly Task _reader;
    private byte[]? _icon;
    private bool _disposed;
    private volatile bool _connected;
    public bool IsConnected => _connected;
    public string PipeName { get; } = $"WindowsIsland.Telegram.{Environment.ProcessId}.{Guid.NewGuid():N}";

    public TelegramHookSession(int processId, Action<IslandNotification> receive)
    {
        _processId = processId;
        _receive = receive;
        _pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 16384, 16384);
        _reader = ReadAsync();
    }

    public async Task AttachAsync(string dllPath, bool fixture = false)
    {
        var executable = await Task.Run(() => TelegramHookInjector.Attach(_processId, dllPath, PipeName, fixture), _stop.Token);
        _icon = await Task.Run(() => new MessengerPopupReader().ReadIcon(executable), _stop.Token);
        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10), _stop.Token);
    }

    private async Task ReadAsync()
    {
        try
        {
            await _pipe.WaitForConnectionAsync(_stop.Token);
            if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var clientId) || clientId != _processId)
                throw new InvalidDataException("Unexpected Telegram bridge process.");
            var header = new byte[TelegramHookProtocol.HeaderSize];
            ulong lastSequence = 0;
            while (!_stop.IsCancellationRequested)
            {
                await _pipe.ReadExactlyAsync(header, _stop.Token);
                var packet = TelegramHookProtocol.ReadHeader(header, _processId);
                if (packet.Kind == 0) { _connected = true; _ready.TrySetResult(); continue; }
                if (packet.Kind == 2) continue;
                var bytes = new byte[packet.PayloadLength];
                await _pipe.ReadExactlyAsync(bytes, _stop.Token);
                if (packet.Sequence <= lastSequence) continue;
                lastSequence = packet.Sequence;
                var (title, body) = TelegramHookProtocol.ReadText(packet, bytes);
                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body)) continue;
                _receive(new((uint)packet.Sequence, DateTimeOffset.FromFileTime(packet.FileTime), "Telegram", title, body,
                    _icon, NotificationSource.ClientHook, "telegram", $"dll/{_processId}/{packet.ObjectId}/{packet.Sequence}/{packet.FileTime}",
                    SenderAvatar: TelegramAvatar.Encode(packet, bytes)));
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or ArgumentException)
        { _ready.TrySetException(error); }
        finally { _connected = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _pipe.Dispose();
        _icon = null;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}
