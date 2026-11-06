using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using WindowsIsland.Services;

namespace WindowsIsland.LogicTests;

internal static class FileDialogProtocolChecks
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (Environment.GetCommandLineArgs().Length != 1) return;
        RunAsync().GetAwaiter().GetResult();
    }

    private static async Task RunAsync()
    {
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            Console.WriteLine("PASS " + name);
        }
        using var requestStream = new MemoryStream();
        var request = new FileDialogRequest(false, "插件包", null, 100, -900, 10, 800, 600);
        await FileDialogProtocol.WriteAsync(requestStream, request, default);
        requestStream.Position = 0;
        Check(await FileDialogProtocol.ReadAsync<FileDialogRequest>(requestStream, default) == request,
            "Dialog IPC preserves Chinese labels, negative monitor positions and the parent hint");
        Check(Encoding.UTF8.GetString(requestStream.ToArray()).Contains("插件包"), "Dialog IPC writes Chinese text as UTF-8");
        foreach (var reply in new[] { new FileDialogReply("ready", 123), new FileDialogReply("complete"),
            new FileDialogReply("complete", Path: @"E:\测试\插件.zip"), new FileDialogReply("error", Error: unchecked((int)0x80004005)) })
        {
            using var stream = new MemoryStream();
            await FileDialogProtocol.WriteAsync(stream, reply, default);
            stream.Position = 0;
            Check(await FileDialogProtocol.ReadAsync<FileDialogReply>(stream, default) == reply, "Dialog IPC preserves " + reply.Kind + " responses");
        }
        foreach (var length in new[] { -1, 0, 128 * 1024 + 1, int.MaxValue })
        {
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, length);
            using var stream = new MemoryStream(header);
            var rejected = false;
            try { await FileDialogProtocol.ReadAsync<FileDialogReply>(stream, default); } catch (IOException) { rejected = true; }
            Check(rejected, "Dialog IPC rejects an invalid packet length before allocation: " + length);
        }
        using var truncated = new MemoryStream([1, 0]);
        var incomplete = false;
        try { await FileDialogProtocol.ReadAsync<FileDialogReply>(truncated, default); } catch (EndOfStreamException) { incomplete = true; }
        Check(incomplete, "Dialog IPC rejects a disconnected partial packet");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var empty = new MemoryStream();
        var stopped = false;
        try { await FileDialogProtocol.ReadAsync<FileDialogReply>(empty, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
        Check(stopped, "Dialog IPC observes cancellation without a result");
    }
}
