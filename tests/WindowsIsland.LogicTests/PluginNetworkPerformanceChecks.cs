using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.LogicTests;

internal static class PluginNetworkPerformanceChecks
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "network-performance", Guid.NewGuid().ToString("N"));
        var paths = new PluginPaths(root);
        var manifest = PluginDirectoryChecks.Manifest with { Permissions = ["network"],
            Network = new(["https://example.com"], "Send own fixture data", ["Fixture data"]) };
        var code = paths.Code(PluginSourceKind.Manual, manifest.Id);
        PluginDirectoryChecks.WritePlugin(code, manifest);
        const long payloadSize = 64L * 1024 * 1024;
        using (var payload = File.Create(Path.Combine(code, "large.bin"))) payload.SetLength(payloadSize);
        var plugin = new ExternalPlugin(paths, manifest, code);
        PluginConsentPolicy.Grant(plugin, PluginConsentPolicy.Describe(plugin));
        var request = new PluginNetworkRequest("https://example.com/fixture", "POST", manifest.Network!.Purpose, Body: [1, 2, 3]);
        var sent = 0;
        PluginNetworkBroker Broker(Func<bool>? active = null) => new(plugin, active ?? (() => true), _ => new Capture(() => sent++));
        // 预热验证及 HTTP 类加载，计量不包含创建大文件和授权的开销。
        await Broker().SendAsync(request);
        var baseline = await Measure(() =>
        {
            if (!PluginConsentPolicy.HasConsent(plugin) || !PluginConsentPolicy.HasConsent(plugin)) throw new InvalidOperationException();
            return Task.CompletedTask;
        });
        var broker = Broker();
        var optimized = await Measure(async () => { await broker.SendAsync(request); });
        Check(optimized.ReadBytes >= (ulong)payloadSize && optimized.ReadBytes < baseline.ReadBytes * 0.75,
            "A valid request still reads the full 64 MiB package while eliminating duplicate package reads");
        var assemblyPath = Path.Combine(code, manifest.EntryAssembly);
        using (var locked = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var before = ReadBytes();
            await Reject<InvalidOperationException>(() => broker.SendAsync(request), "Rate-limited requests reject before reading locked package files");
            Check(ReadBytes() - before < 1024 * 1024, "Rate-limited requests perform no whole-package scan");
            await Reject<InvalidDataException>(() => Broker().SendAsync(request with { Body = new byte[PluginNetworkLimits.MaximumRequestBytes + 1] }),
                "Oversized bodies reject before copying buffers or hashing package files");
            await Reject<InvalidDataException>(() => Broker().SendAsync(request with { Purpose = "not disclosed" }),
                "Undisclosed purposes reject before package I/O");
            await Reject<UnauthorizedAccessException>(() => Broker().SendAsync(request), "A valid request fails closed when actual package verification cannot read a file");
        }
        foreach (var mutation in new Action[]
        {
            () => PluginConsentPolicy.Revoke(plugin),
            () => PluginConsentPolicy.WriteGeneration(root, manifest.Id, Guid.NewGuid().ToString("N")),
            () => File.WriteAllText(Path.Combine(code, "manifest.json"), JsonSerializer.Serialize(manifest with { Name = "changed" }, PluginManifestReader.Json))
        })
        {
            var before = sent;
            await Reject<UnauthorizedAccessException>(() => Broker().SendAsync(request with { Headers = new MutatingHeaders(mutation) }),
                "Revocation, installation changes and disclosure changes during request preparation block transport");
            Check(sent == before, "Failed final authorization never constructs an outbound transport request");
            PluginConsentPolicy.WriteGeneration(root, manifest.Id, null);
            File.WriteAllText(Path.Combine(code, "manifest.json"), JsonSerializer.Serialize(manifest, PluginManifestReader.Json));
            PluginConsentPolicy.Grant(plugin, PluginConsentPolicy.Describe(plugin));
        }
        var original = File.ReadAllBytes(assemblyPath);
        var stamp = File.GetLastWriteTimeUtc(assemblyPath);
        var changed = original.ToArray();
        changed[^1] ^= 1;
        File.WriteAllBytes(assemblyPath, changed);
        File.SetLastWriteTimeUtc(assemblyPath, stamp);
        await Reject<UnauthorizedAccessException>(() => Broker().SendAsync(request), "Same-length code tampering with restored mtime is rejected without a hash cache");
        File.WriteAllBytes(assemblyPath, original);
        using (var snapshot = PluginRunSnapshot.Open(plugin))
        {
            var session = new PluginNetworkBroker(plugin, () => true, _ => new Capture(() => sent++), snapshot);
            var held = await Measure(async () => { await session.SendAsync(request); });
            Check(held.ReadBytes < 1024 * 1024, "A locked running session sends without rereading the 64 MiB package");
            var blocked = false;
            try { File.WriteAllBytes(assemblyPath, changed); } catch (IOException) { blocked = true; }
            Check(blocked, "Stable read handles prevent same-length code tampering during a session");
            File.WriteAllText(Path.Combine(code, "unexpected.bin"), "fixture");
            Check(!snapshot.IsCurrent(), "Adding a tree member invalidates the running session");
            File.Delete(Path.Combine(code, "unexpected.bin"));
            Check(!snapshot.IsCurrent(), "An invalidated snapshot cannot revive after removing the unexpected member");
            Console.WriteLine("PERF locked-session " + JsonSerializer.Serialize(held));
        }
        foreach (var mutation in new Action[] { () => PluginConsentPolicy.Revoke(plugin),
            () => PluginConsentPolicy.WriteGeneration(root, manifest.Id, Guid.NewGuid().ToString("N")) })
        {
            using var snapshot = PluginRunSnapshot.Open(plugin);
            Check(snapshot.IsCurrent(), "Each authorization mutation begins with an independently valid snapshot");
            mutation();
            Check(!snapshot.IsCurrent(), "Revocation or installation generation changes invalidate a previously valid session");
            PluginConsentPolicy.WriteGeneration(root, manifest.Id, null);
            PluginConsentPolicy.Grant(plugin, PluginConsentPolicy.Describe(plugin));
        }
        PluginConsentPolicy.WriteGeneration(root, manifest.Id, null);
        PluginConsentPolicy.Grant(plugin, PluginConsentPolicy.Describe(plugin));
        var activeNow = true;
        await Reject<UnauthorizedAccessException>(() => Broker(() => activeNow).SendAsync(request with
            { Headers = new MutatingHeaders(() => activeNow = false) }), "A context disabled during request preparation cannot send");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Reject<OperationCanceledException>(() => Broker().SendAsync(request, canceled.Token), "Canceled requests never enter package validation or transport");
        var report = JsonSerializer.Serialize(new { PayloadBytes = payloadSize, BaselineTwoConsentChecks = baseline, OptimizedRequest = optimized });
        File.WriteAllText(Path.Combine(root, "performance.json"), report);
        Console.WriteLine("PERF " + report);
    }

    private sealed record Measurement(ulong ReadBytes, double ElapsedMs, double CpuMs);
    private static async Task<Measurement> Measure(Func<Task> action)
    {
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var bytes = ReadBytes();
        var timer = Stopwatch.StartNew();
        await action();
        return new(ReadBytes() - bytes, timer.Elapsed.TotalMilliseconds, (process.TotalProcessorTime - cpu).TotalMilliseconds);
    }
    private static ulong ReadBytes()
    {
        if (!GetProcessIoCounters(new nint(-1), out var counters)) throw new System.ComponentModel.Win32Exception();
        return counters.ReadTransferCount;
    }
    private static async Task Reject<T>(Func<Task> action, string message) where T : Exception
    {
        var rejected = false;
        try { await action(); } catch (T) { rejected = true; }
        Check(rejected, message);
    }
    private static void Check(bool value, string message) => PluginDirectoryChecks.Check(value, message);
    private sealed class Capture(Action send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            send();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) });
        }
    }
    private sealed class MutatingHeaders(Action mutate) : IReadOnlyDictionary<string, string>
    {
        public int Count => 1;
        public IEnumerable<string> Keys => ["X-Test"];
        public IEnumerable<string> Values => ["fixture"];
        public string this[string key] => key == "X-Test" ? "fixture" : throw new KeyNotFoundException();
        public bool ContainsKey(string key) => key == "X-Test";
        public bool TryGetValue(string key, out string value) { value = "fixture"; return ContainsKey(key); }
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            mutate();
            yield return new("X-Test", "fixture");
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessIoCounters(nint process, out IoCounters counters);
}
