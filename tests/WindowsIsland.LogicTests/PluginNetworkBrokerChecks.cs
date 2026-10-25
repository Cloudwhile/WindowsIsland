using System.Net;
using System.Text;
using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.LogicTests;

internal static class PluginNetworkBrokerChecks
{
    public static async Task RunAsync()
    {
        var paths = new PluginPaths(Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "network-broker", Guid.NewGuid().ToString("N")));
        var manifest = new PluginManifest("wini_plugin_network_transport", "Network transport fixture", "1.0.0", "fixture.dll", "Fixture", 1,
            [PluginPermissions.Network], Network: new(["https://example.com"], "Send fixed own canary", ["Own fixture text"]));
        var code = paths.Code(PluginSourceKind.Manual, manifest.Id);
        Directory.CreateDirectory(code);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixture", "WindowsIsland.PluginFixture.dll"), Path.Combine(code, "fixture.dll"));
        File.WriteAllText(Path.Combine(code, "manifest.json"), JsonSerializer.Serialize(manifest, PluginManifestReader.Json));
        var plugin = new ExternalPlugin(paths, manifest, code);
        PluginConsentPolicy.Grant(plugin, PluginConsentPolicy.Describe(plugin));
        var original = Encoding.UTF8.GetBytes("插件自己的固定测试正文");
        var body = original.ToArray();
        var headers = new Dictionary<string, string> { ["X-Fixture"] = "approved value", ["Content-Type"] = "text/plain" };
        var sent = 0;
        {
            var broker = new PluginNetworkBroker(plugin, () => true, settings =>
            {
                body[0] ^= 0xff;
                headers["X-Fixture"] = "changed after snapshot";
                Check(!settings.UseProxy && !settings.UseCookies && !settings.AllowAutoRedirect && settings.Credentials is null
                    && settings.AutomaticDecompression == DecompressionMethods.None && settings.ConnectCallback is not null,
                    "Production transport disables redirects, ambient credentials, proxies and cookies and pins validated connections");
                return new Capture(async (message, token) =>
                {
                    sent++;
                    Check(message.Method == HttpMethod.Post && message.RequestUri!.AbsoluteUri == "https://example.com/own?value=fixed"
                        && message.Headers.GetValues("X-Fixture").Single() == "approved value"
                        && (await message.Content!.ReadAsByteArrayAsync(token)).AsSpan().SequenceEqual(original),
                        "Host-authorized networking uses an immutable snapshot of plugin-owned buffers");
                    return new(HttpStatusCode.OK) { Content = new StringContent("fixed simulated server response") };
                });
            });
            var response = await broker.SendAsync(new("https://example.com/own?value=fixed", "POST", manifest.Network!.Purpose, headers, body));
            Check(sent == 1 && response.StatusCode == 200 && Encoding.UTF8.GetString(response.Body) == "fixed simulated server response",
                "Enable-time consent permits a background request without a per-request UI callback or external upload");

            foreach (var address in new[] { "127.0.0.1", "::1", "10.1.2.3", "169.254.169.254", "172.16.0.1", "192.168.0.1",
                "100.100.100.200", "168.63.129.16", "224.0.0.1", "::ffff:127.0.0.1", "fc00::1", "fe80::1", "2002:7f00:1::" })
                Check(!PluginNetworkBroker.IsPublicAddress(IPAddress.Parse(address)), "The outbound address policy rejects " + address);
            Check(PluginNetworkBroker.IsPublicAddress(IPAddress.Parse("8.8.8.8"))
                && PluginNetworkBroker.IsPublicAddress(IPAddress.Parse("2606:4700:4700::1111")), "The outbound policy retains public IPv4 and IPv6 destinations");
        }
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); Console.WriteLine("PASS " + message); }
    private sealed class Capture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
