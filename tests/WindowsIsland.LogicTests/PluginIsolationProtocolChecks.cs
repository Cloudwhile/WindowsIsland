using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using WindowsIsland.PluginIsolation;

namespace WindowsIsland.LogicTests;

internal static class PluginIsolationProtocolChecks
{
    public static async Task RunAsync()
    {
        var name = "WindowsIsland.ProtocolFixture." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync(2000));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new IsolationRpc(server, server, async (method, value, token) =>
        {
            if (method == "echo") return value.Clone();
            if (method != "wait") throw new InvalidDataException();
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return null;
        });
        using var peer = new IsolationRpc(client, client, (_, _, _) => throw new InvalidDataException());
        var echo = await peer.CallAsync<string>("echo", "管道只传声明式数据");
        Check(echo == "管道只传声明式数据", "Sandbox RPC preserves bounded UTF-8 contract data");
        using (var cancellation = new CancellationTokenSource())
        {
            var request = peer.CallAsync<JsonElement>("wait", token: cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            try { await request.WaitAsync(TimeSpan.FromSeconds(2)); throw new InvalidOperationException("Request was not cancelled."); }
            catch (OperationCanceledException) { }
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Check(true, "Sandbox request cancellation reaches the host handler before an approval can continue");
        Check(await peer.CallAsync<string>("echo", "after cancellation") == "after cancellation",
            "A late cancelled response cannot corrupt the next RPC request");
        try
        {
            await peer.CallAsync<JsonElement>("echo", new string('x', IsolationRpc.MaxFrame + 1));
            throw new InvalidOperationException("Oversized packet was accepted.");
        }
        catch (InvalidDataException) { }
        Check(true, "Oversized outgoing RPC messages are rejected before transmission");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, IsolationRpc.MaxFrame + 1);
        await client.WriteAsync(prefix);
        // The unbuffered write delivers the frame; the RPC reader may dispose the pipe as soon as the host rejects it.
        await host.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Check(true, "An oversized untrusted frame closes the private channel without allocating its payload");

        using var stalled = new CancelledStream();
        using var stalledRpc = new IsolationRpc(stalled, stalled, (_, _, _) => Task.FromResult<object?>(null));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try { await stalledRpc.CallAsync<string>("echo", "bounded cancellation", deadline.Token).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (OperationCanceledException) { }
        Check(clock.Elapsed < TimeSpan.FromSeconds(2), "Cancellation stays bounded when the worker stops reading the pipe");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }

    private sealed class CancelledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await Task.Delay(Timeout.Infinite, token); return 0; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { await Task.Delay(Timeout.Infinite, token); }
    }
}
