using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using static WindowsIsland.LogicTests.PluginCatalogContractChecks;

namespace WindowsIsland.LogicTests;

internal static class PluginPlatformContractChecks
{
    private static void Check(bool condition, string message) => PluginCatalogContractChecks.Check(condition, message);
    public static async Task RunAsync(string? platformOutput = null)
    {
        using var input = typeof(PluginPlatformContractChecks).Assembly.GetManifestResourceStream(
            "WindowsIsland.LogicTests.Fixtures.plugin-contract.json")!;
        using var fixture = JsonDocument.Parse(input);
        var root = fixture.RootElement;
        var source = root.GetProperty("source").Deserialize<PluginSource>(PluginManifestReader.Json)!;
        var entry = root.GetProperty("entry").Deserialize<PluginCatalogEntry>(PluginManifestReader.Json)!;
        var manifest = root.GetProperty("manifest").Deserialize<PluginManifest>(PluginManifestReader.Json)!;
        PluginCatalogClient.ValidateSource(source, new(PluginCatalogClient.DefaultBaseUrl));
        PluginCatalogClient.ValidateEntry(entry, source);
        PluginManifestReader.Validate(manifest);
        PluginLibrary.ValidatePackage(manifest, entry);
        foreach (var group in new[] { "versions", "repositories", "downloads", "permissions", "networks" })
        {
            foreach (var test in root.GetProperty(group).EnumerateArray())
            {
                var value = test.GetProperty("value");
                var accepted = group switch
                {
                    "versions" => CatalogVersion.TryParse(value.GetString(), out _),
                    "repositories" => PluginCatalogPolicy.Repository(value.GetString()),
                    "downloads" => !Reject(() => PluginCatalogClient.ValidateEntry(entry with { DownloadUrl = value.GetString()! }, source)),
                    "permissions" => !Reject(() => PluginCatalogClient.ValidateEntry(entry with { Permissions = value.Deserialize<string[]>()!, Network = null }, source)),
                    _ => !Reject(() => PluginManifestReader.ValidateSecurityMetadata(manifest with
                        { Network = value.Deserialize<PluginNetworkDisclosure>(PluginManifestReader.Json) }))
                };
                Check(accepted == test.GetProperty("valid").GetBoolean(), "Platform contract: " + group + " " + value.GetRawText());
            }
        }
        Check(Reject(() => PluginLibrary.ValidatePackage(manifest with { MinimumHostVersion = "0.2.0" }, entry), PluginErrorCode.IndexMismatch),
            "Platform contract: package and directory minimum host versions must agree");
        Check(Reject(() => PluginLibrary.ValidatePackage(manifest with { ContractVersion = 2 }, entry), PluginErrorCode.IndexMismatch),
            "Platform contract: package and directory contract versions must agree");
        PluginLibrary.ValidatePackage(manifest with { MinimumHostVersion = null }, entry with { MinHostVersion = "0.0.0" });
        var custom = manifest with { Permissions = ["notifications", "settings", "custom", "storage", "clipboard"], Network = null };
        PluginManifestReader.Validate(custom);
        PluginCatalogClient.ValidateEntry(entry with { Permissions = custom.Permissions, Network = null }, source);
        var trust = PluginTrust.Create(custom);
        Check(trust.Granted.Order().SequenceEqual(new[] { "notifications", "settings" }.Order())
            && trust.Denied.Order().SequenceEqual(new[] { "custom", "storage", "clipboard" }.Order()),
            "Platform contract: developer-defined permission labels are accepted without granting system capabilities");
        if (platformOutput is null) return;
        var transport = new Transport
        {
            Sources = await File.ReadAllTextAsync(Path.Combine(platformOutput, "sources.json")),
            Plugins = await File.ReadAllTextAsync(Path.Combine(platformOutput, "plugins.json"))
        };
        using var client = new PluginCatalogClient(transport);
        var fetched = await client.FetchAsync(PluginCatalogClient.DefaultBaseUrl, default);
        Check(fetched.Failures.Length == 0 && fetched.Indices.Length == 1, "Platform serialization: actual Rust source and plugin responses are accepted by the host");
        var package = JsonSerializer.Deserialize<PluginManifest>(await File.ReadAllTextAsync(Path.Combine(platformOutput, "manifest.json")), PluginManifestReader.Json)!;
        PluginManifestReader.Validate(package);
        PluginLibrary.ValidatePackage(package, fetched.Indices.Single().Plugins.Single());
        Check(true, "Platform serialization: actual Rust manifest and catalog metadata agree");
    }
}
