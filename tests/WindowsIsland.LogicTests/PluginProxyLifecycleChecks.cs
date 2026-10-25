using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.LogicTests;

internal static class PluginProxyLifecycleChecks
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var mode in new[] { "hangStart", "hangStopping", "hangStop" }) await RunCaseAsync(mode);
    }
    private static async Task RunCaseAsync(string mode)
    {
        var paths = new PluginPaths(Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "proxy-lifecycle", Guid.NewGuid().ToString("N")));
        var stopping = mode == "hangStopping";
        var id = stopping ? "wini_plugin_security_stopping" : "wini_plugin_security_lifecycle";
        var manifest = new PluginManifest(id, "Proxy lifecycle fixture", "1.0.0", "WindowsIsland.PluginSecurityFixture.dll",
            "WindowsIsland.PluginSecurityFixture." + (stopping ? "StoppingSource" : "LifecycleSource"), 1, [PluginPermissions.Settings]);
        var code = paths.Code(PluginSourceKind.Manual, id);
        Directory.CreateDirectory(code);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixture", manifest.EntryAssembly), Path.Combine(code, manifest.EntryAssembly));
        File.WriteAllText(Path.Combine(code, "manifest.json"), JsonSerializer.Serialize(manifest, PluginManifestReader.Json));
        var external = new ExternalPlugin(paths, manifest, code);
        PluginConsentPolicy.Grant(external, PluginConsentPolicy.Describe(external));
        var settingsStore = new SettingsStore(PluginConsentPolicy.HostSettingsPath(external));
        settingsStore.SetPluginBoolean(id, mode, true);
        using var context = new PluginContext(id, new PluginSettings(settingsStore, id), new PluginDiagnostics(id, DiagnosticArea.Application), _ => { });
        var proxy = (SandboxPluginProxy)external.Create();
        try
        {
            await proxy.InitializeAsync(context).WaitAsync(TimeSpan.FromSeconds(15));
            Task? starting = null;
            if (mode == "hangStart")
            {
                starting = proxy.StartAsync();
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (!File.Exists(Path.Combine(external.DataDirectory, "start-entered.marker")))
                { if (DateTime.UtcNow >= deadline) throw new TimeoutException(); await Task.Delay(20); }
                Check(!starting.IsCompleted, "The real proxy has a pending uncooperative start before revocation");
            }
            else await proxy.StartAsync();
            var clock = Stopwatch.StartNew();
            context.Dispose();
            proxy.Revoke();
            try { await proxy.OnStoppingAsync(PluginStopReason.Disabled); } catch (OperationCanceledException) { }
            try { await proxy.StopAsync(); } catch (OperationCanceledException) { }
            proxy.Dispose();
            await proxy.Termination.WaitAsync(TimeSpan.FromSeconds(8));
            if (starting is not null) try { await starting; } catch (OperationCanceledException) { }
            Check(clock.Elapsed < TimeSpan.FromSeconds(8), "The production proxy's command lock and complete cleanup budget remain bounded: " + mode);
        }
        finally
        {
            external.Unload();
            await external.VerifyUnloadAsync().WaitAsync(TimeSpan.FromSeconds(9));
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.Root.ToUpperInvariant() + "\n" + id)))[..40].ToLowerInvariant();
            Marshal.ThrowExceptionForHR(DeleteAppContainerProfile("WindowsIsland.Plugin." + digest));
        }
        Check(true, "Confirmed proxy termination permits code removal without a mapped-worker race: " + mode);
        Directory.Delete(code, recursive: true);
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); Console.WriteLine("PASS " + message); }
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeleteAppContainerProfile(string profile);
}
