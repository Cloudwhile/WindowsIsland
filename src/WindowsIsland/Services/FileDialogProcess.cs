using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace WindowsIsland.Services;

internal static class FileDialogProcess
{
    private const string Command = "--file-dialog-helper";
    private const string PipePrefix = "WindowsIsland.FileDialog.";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static Task<string?> PickAsync(nint owner, bool save, string fileType, string? fileName, CancellationToken token) =>
        Task.Run(async () =>
        {
            await Gate.WaitAsync(token).ConfigureAwait(false);
            try { return await RunAsync(owner, save, fileType, fileName, token).ConfigureAwait(false); }
            finally { Gate.Release(); }
        }, token);

    private static async Task<string?> RunAsync(nint owner, bool save, string fileType, string? fileName, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!GetWindowRect(owner, out var bounds)) throw new IOException("The file dialog parent has closed.");
        var request = new FileDialogRequest(save, fileType, fileName, owner.ToInt64(), bounds.Left, bounds.Top,
            bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        var name = PipePrefix + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var current = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add(Command);
        start.ArgumentList.Add(current.Id.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(name);
        using var worker = Process.Start(start) ?? throw new IOException("The file dialog helper did not start.");
        Task<FileDialogReply>? reading = null;
        try
        {
            await pipe.WaitForConnectionAsync(token).WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            await FileDialogProtocol.WriteAsync(pipe, request, token).ConfigureAwait(false);
            reading = FileDialogProtocol.ReadAsync<FileDialogReply>(pipe, token);
            var startup = Stopwatch.StartNew();
            var unresponsive = Stopwatch.StartNew();
            nint dialog = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                await Task.WhenAny(reading, Task.Delay(250, token)).ConfigureAwait(false);
                if (reading.IsCompleted)
                {
                    var reply = await reading.ConfigureAwait(false);
                    if (reply.Kind == "ready")
                    {
                        if (dialog != 0 || reply.Window == 0) throw new IOException("Invalid file dialog readiness packet.");
                        dialog = (nint)reply.Window;
                        GetWindowThreadProcessId(dialog, out var process);
                        if (process != worker.Id) throw new IOException("Invalid file dialog window.");
                        unresponsive.Restart();
                        reading = FileDialogProtocol.ReadAsync<FileDialogReply>(pipe, token);
                        continue;
                    }
                    if (reply.Kind == "error") throw new IOException("The file dialog helper failed.", reply.Error);
                    if (reply.Kind != "complete" || reply.Path is { Length: > 32767 }) throw new IOException("Invalid file dialog result.");
                    if (reply.Path is { } path && !Path.IsPathFullyQualified(path)) throw new IOException("Invalid file dialog path.");
                    token.ThrowIfCancellationRequested();
                    return reply.Path;
                }
                if (dialog == 0)
                {
                    if (startup.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("The file dialog did not open.");
                }
                else if (SendMessageTimeout(dialog, 0, 0, 0, 0x23, 150, out _) != 0) unresponsive.Restart();
                else if (unresponsive.Elapsed > TimeSpan.FromSeconds(6)) throw new TimeoutException("The file dialog stopped responding.");
                // 退出由管道 EOF 结束读取，不能因进程先退出而丢弃已写入的最终结果。
            }
        }
        finally
        {
            // 只回收本次创建并持有进程句柄的辅助进程，主界面不等待原生线程退出。
            pipe.Dispose();
            if (reading is not null)
            {
                // 断开时收集未完成读取的异常，保留原本的取消或超时结果。
                try { await reading.ConfigureAwait(false); } catch (Exception) { }
            }
            try
            {
                if (!worker.HasExited) worker.Kill();
                await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception or TimeoutException) { }
        }
    }

    public static bool TryHandle(string[] arguments, out int result)
    {
        result = 0;
        if (arguments.Length == 0 || arguments[0] != Command) return false;
        result = 2;
        if (arguments.Length != 4 || !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parentId)
            || !long.TryParse(arguments[2], NumberStyles.None, CultureInfo.InvariantCulture, out var started)
            || !arguments[3].StartsWith(PipePrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(arguments[3][PipePrefix.Length..], "N", out _)) return true;
        try
        {
            using var parent = Process.GetProcessById(parentId);
            if (parent.Id == Environment.ProcessId || parent.StartTime.ToUniversalTime().Ticks != started
                || !string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return true;
            using var pipe = new NamedPipeClientStream(".", arguments[3], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            pipe.Connect(10000);
            using var lifetime = new CancellationTokenSource();
            var request = FileDialogProtocol.ReadAsync<FileDialogRequest>(pipe, lifetime.Token)
                .WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            if (request.FileType is not { Length: > 0 and <= 200 } || request.FileName is { Length: > 200 }
                || request.Width is < 1 or > 32768 || request.Height is < 1 or > 32768) return true;
            var window = (nint)request.ParentWindow;
            GetWindowThreadProcessId(window, out var ownerProcess);
            if (ownerProcess != parentId) return true;
            _ = ExitWithParentAsync(parent, pipe, lifetime.Token);
            try
            {
                var path = NativeFileDialog.Show(request, dialog => FileDialogProtocol.WriteAsync(pipe,
                    new FileDialogReply("ready", dialog.ToInt64()), lifetime.Token).GetAwaiter().GetResult());
                FileDialogProtocol.WriteAsync(pipe, new FileDialogReply("complete", Path: path), lifetime.Token).GetAwaiter().GetResult();
                result = 0;
            }
            catch (Exception error)
            {
                FileDialogProtocol.WriteAsync(pipe, new FileDialogReply("error", Error: error.HResult), lifetime.Token).GetAwaiter().GetResult();
                result = 1;
            }
            finally { lifetime.Cancel(); }
        }
        catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException or ArgumentException
            or TimeoutException or OperationCanceledException or System.Text.Json.JsonException) { result = 1; }
        return true;
    }

    private static async Task ExitWithParentAsync(Process parent, Stream pipe, CancellationToken token)
    {
        try
        {
            var closed = pipe.ReadAsync(new byte[1], token).AsTask();
            await Task.WhenAny(parent.WaitForExitAsync(token), closed).ConfigureAwait(false);
            if (!token.IsCancellationRequested) Environment.Exit(2);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException) { }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect bounds);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] private static extern nint SendMessageTimeout(nint window,
        uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
}
