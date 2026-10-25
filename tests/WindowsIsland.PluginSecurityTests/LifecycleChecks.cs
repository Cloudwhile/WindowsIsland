using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WindowsIsland.Contracts;
using WindowsIsland.PluginIsolation;
using WindowsIsland.Plugins;

namespace WindowsIsland.PluginSecurityTests;

internal static class LifecycleChecks
{
    public static async Task RunAsync(string fixture, string root, CancellationToken token, Action<bool, string> check)
    {
        foreach (var mode in new[] { "normal", "hangStart", "hangStopping", "hangStop", "suspended" })
            await RunCaseAsync(fixture, root, mode, token, check);
    }

    private static async Task RunCaseAsync(string fixture, string root, string mode, CancellationToken token, Action<bool, string> check)
    {
        var caseRoot = Path.Combine(root, "lifecycle", mode);
        var code = Path.Combine(caseRoot, "code");
        var data = Path.Combine(caseRoot, "data");
        Directory.CreateDirectory(code); Directory.CreateDirectory(data);
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(fixture)!).Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)))
            File.Copy(file, Path.Combine(code, Path.GetFileName(file)), true);
        var stopping = mode == "hangStopping";
        var manifest = new PluginManifest(stopping ? "wini_plugin_security_stopping" : "wini_plugin_security_lifecycle", "Lifecycle fixture", "1.0.0",
            Path.GetFileName(fixture), "WindowsIsland.PluginSecurityFixture." + (stopping ? "StoppingSource" : "LifecycleSource"), PluginContract.Version,
            [PluginPermissions.Settings]);
        var plugin = new ExternalPlugin(manifest, Path.Combine(root, "host-private"), code, data);
        var host = new FixtureHost();
        host.Settings[mode] = true;
        var process = new SandboxProcess(plugin);
        var pid = process.ProcessId;
        using var rpc = new IsolationRpc(process.Input, process.Output, host.HandleAsync);
        try
        {
            await process.VerifyReadyAsync(rpc, token);
            await rpc.CallAsync<WorkerDescription>("initialize", new WorkerInitialization(manifest, data, manifest.Permissions, null, CodeDirectory: code), token);
            check(File.Exists(Path.Combine(data, "initialized.marker")), mode + ": isolated initialization reached the controlled marker");
            if (mode == "hangStart")
                await CancelCommandAsync(rpc, "start", null, Path.Combine(data, "start-entered.marker"), token, check);
            else
            {
                await rpc.CallAsync<WorkerDescription>("start", token: token);
                if (mode == "hangStopping")
                    await CancelCommandAsync(rpc, "stopping", PluginStopReason.Disabled, Path.Combine(data, "stopping-entered.marker"), token, check);
                else if (mode == "hangStop")
                    await CancelCommandAsync(rpc, "stop", null, Path.Combine(data, "stop-entered.marker"), token, check);
                else if (mode == "normal")
                {
                    await rpc.CallAsync<WorkerDescription>("stop", token: token);
                    check(File.Exists(Path.Combine(data, "stop-entered.marker")), "normal: stop completed inside the private worker");
                }
                else
                {
                    using var suspended = new PrivateSuspension(process);
                    check(suspended.ThreadCount > 0, "suspended: only the created private worker threads were suspended");
                    await CancelCommandAsync(rpc, "poll", null, null, token, check);
                    await ReclaimAsync(process, pid, mode, token, check);
                    return;
                }
            }
            await ReclaimAsync(process, pid, mode, token, check);
        }
        finally
        {
            process.Dispose();
            await process.ExitTask.WaitAsync(TimeSpan.FromSeconds(6), token);
        }
    }

    private static async Task CancelCommandAsync(IsolationRpc rpc, string method, object? payload, string? enteredMarker,
        CancellationToken token, Action<bool, string> check)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        var operation = rpc.CallAsync<WorkerDescription>(method, payload, deadline.Token);
        if (enteredMarker is not null)
        {
            using var entered = CancellationTokenSource.CreateLinkedTokenSource(token);
            entered.CancelAfter(TimeSpan.FromSeconds(5));
            while (!File.Exists(enteredMarker)) await Task.Delay(20, entered.Token);
            check(File.Exists(enteredMarker), method + ": the controlled hanging hook actually started");
        }
        else await Task.Delay(100, token);
        check(!operation.IsCompleted, method + ": the real worker command is still pending before cancellation");
        deadline.Cancel();
        var cancelled = false;
        try { await operation.WaitAsync(TimeSpan.FromSeconds(2), token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { cancelled = true; }
        check(cancelled, method + ": a genuinely pending worker command observes the host cancellation deadline");
    }

    private static async Task ReclaimAsync(SandboxProcess process, int pid, string mode, CancellationToken token, Action<bool, string> check)
    {
        var watch = Stopwatch.StartNew();
        process.Dispose();
        await process.ExitTask.WaitAsync(TimeSpan.FromSeconds(6), token);
        check(watch.Elapsed < TimeSpan.FromSeconds(6), mode + ": kill and confirmed exit finish within the bounded reclamation budget");
        var running = false;
        try { using var native = Process.GetProcessById(pid); running = !native.HasExited; }
        catch (ArgumentException) { }
        check(!running, mode + ": no private worker survives the acknowledged exit task");
    }

    private sealed class PrivateSuspension : IDisposable
    {
        private readonly List<nint> _threads = [];
        public int ThreadCount => _threads.Count;
        public PrivateSuspension(SandboxProcess sandbox)
        {
            // PID 来自此夹具刚创建且仍持有句柄的进程，不接受用户 PID 或正式客户端。
            using var process = Process.GetProcessById(sandbox.ProcessId);
            var seen = new HashSet<int>();
            try
            {
                for (var pass = 0; pass < 3; pass++)
                {
                    process.Refresh();
                    foreach (ProcessThread thread in process.Threads)
                    {
                        using (thread)
                        {
                            if (!seen.Add(thread.Id)) continue;
                            var handle = OpenThread(0x0802, false, thread.Id);
                            if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                            if (GetProcessIdOfThread(handle) != (uint)sandbox.ProcessId)
                            { CloseHandle(handle); throw new InvalidOperationException("The private worker thread identity changed."); }
                            if (SuspendThread(handle) == uint.MaxValue)
                            { CloseHandle(handle); throw new Win32Exception(Marshal.GetLastWin32Error()); }
                            _threads.Add(handle);
                        }
                    }
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            foreach (var thread in _threads) { ResumeThread(thread); CloseHandle(thread); }
            _threads.Clear();
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int id);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetProcessIdOfThread(nint thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint SuspendThread(nint thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    }
}
