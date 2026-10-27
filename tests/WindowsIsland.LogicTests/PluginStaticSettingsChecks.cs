using System.IO.Compression;
using System.Runtime.InteropServices;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;
using static WindowsIsland.LogicTests.PluginDirectoryChecks;

namespace WindowsIsland.LogicTests;

internal static class PluginStaticSettingsChecks
{
    private static void Check(bool value, string message) => PluginDirectoryChecks.Check(value, message);
    private const string Valid = """
        {"schemaVersion":1,"fields":[
          {"key":"endpoint","type":"string","title":"服务地址","default":"https://example.com"},
          {"key":"enabled","type":"boolean","title":"接收消息","default":true}]}
        """;

    public static async Task RunAsync()
    {
        await PluginTypedSettingsChecks.RunAsync();
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification", "static-settings", Guid.NewGuid().ToString("N"));
        var paths = new PluginPaths(Path.Combine(root, "Plugins"));
        var store = new SettingsStore(Path.Combine(root, "settings.json"));
        var manifest = Manifest with { EntryType = "WindowsIsland.PluginFixture.BrokenSource", Permissions = [] };
        var installer = new PluginInstaller(paths);
        var zip = Package(Valid);
        using var manager = new PluginManager([], store);
        manager.ConfigureDirectories(paths);
        await manager.StartAsync();
        using (var prepared = await installer.PrepareAsync(zip)) await manager.InstallAsync(prepared);
        var external = manager.Snapshot().Single().Definition.Metadata!.External!;
        var model = await manager.GetSettingsAsync(manifest.Id);
        Check(model is { Fields.Count: 2 } && model.Fields[0].DefaultValue as string == "https://example.com"
            && model.Fields[1].DefaultValue is true && !PluginConsentPolicy.HasConsent(external)
            && manager.Snapshot().Single().StatusKey == "Off",
            "Unapproved disabled plugin with a throwing constructor exposes static fields without executing DLL code");
        model!.Settings.SetString("endpoint", "https://configured.example");
        model.Settings.SetBoolean("enabled", false);
        Check(!store.GetPluginBoolean(manifest.Id, "Host.Enabled", false) && !PluginConsentPolicy.HasConsent(external),
            "Preconfiguration neither enables a plugin nor grants consent");
        await manager.ShutdownAsync();
        using (var restarted = new PluginManager([], store))
        {
            restarted.ConfigureDirectories(paths);
            await restarted.StartAsync();
            var restored = await restarted.GetSettingsAsync(manifest.Id);
            Check(restored?.Settings.GetString("endpoint") == "https://configured.example"
                && !restored.Settings.GetBoolean("enabled", true),
                "Disabled static settings persist across manager restart without settings permission");
            restored!.Settings.SetString("endpoint", "https://edited.example");
            await restarted.ShutdownAsync();
        }
        var schemaPath = Path.Combine(external.Directory, PluginStaticSettings.FileName);
        PluginConsentPolicy.Grant(external, PluginConsentPolicy.Describe(external));
        File.WriteAllText(schemaPath, Valid.Replace("接收消息", "新的标题"));
        Check(!PluginConsentPolicy.HasConsent(external), "Static schema changes invalidate existing package consent");
        File.WriteAllText(schemaPath, Valid);
        var invalidCases = new[]
        {
            "{}", "[]", "{", Valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":3"),
            Valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":\"1\""),
            Valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
            Valid.Replace("\"endpoint\"", "\"Host.Enabled\""),
            Valid.Replace("\"key\":\"enabled\"", "\"key\":\"endpoint\""),
            Valid.Replace("\"default\":true", "\"default\":\"true\""),
            Valid.Replace("\"type\":\"string\"", "\"type\":\"password\""),
            Valid.Replace("\"type\":\"string\"", "\"type\":\"string\",\"secret\":true"),
            Valid.Replace("\"title\":\"服务地址\"", "\"title\":\" \""),
            Valid.Replace("https://example.com", new string('x', 4097)),
            "{\"schemaVersion\":1,\"fields\":[" + string.Join(',', Enumerable.Repeat("{}", 65)) + "]}",
            new string(' ', 65537)
        };
        foreach (var invalid in invalidCases)
        {
            var rejected = false;
            try { using var prepared = await installer.PrepareAsync(Package(invalid)); }
            catch (PluginException) { rejected = true; }
            Check(rejected, "Malformed, unsupported or oversized static schemas are rejected during package preparation");
        }
        using (var inspection = new PluginManager([], store))
        {
            inspection.ConfigureDirectories(paths);
            await inspection.RescanAsync();
            Check(await inspection.GetSettingsAsync(manifest.Id) is not null, "Valid static schema remains available before starting the manager");
            File.WriteAllText(schemaPath, "{}");
            Check(await inspection.GetSettingsAsync(manifest.Id) is null, "Damaged static schema does not fall through to plugin code");
            await inspection.ShutdownAsync();
        }
        File.Delete(schemaPath);
        Check(PluginStaticSettings.Read(external.Directory) is null, "Legacy packages without a static schema keep the dynamic path");
        File.WriteAllText(schemaPath, "{\"schemaVersion\":1,\"fields\":[]}");
        Check(PluginStaticSettings.Read(external.Directory)?.Count == 0, "An explicit empty static schema is valid");
        if (OperatingSystem.IsWindows())
        {
            File.Delete(schemaPath);
            var target = Path.Combine(root, "schema-canary.json");
            File.WriteAllText(target, Valid);
            Check(CreateHardLink(schemaPath, target, 0), "A private static-schema hard-link canary is prepared");
            var rejected = false;
            try { PluginStaticSettings.Read(external.Directory); } catch (PluginException) { rejected = true; }
            Check(rejected && File.ReadAllText(target) == Valid, "Static schema hard links are rejected without modifying the target");
            File.Delete(schemaPath);
        }

        string Package(string content)
        {
            var path = PluginInstallationChecks.Zip(root, manifest);
            using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
            using var writer = new StreamWriter(archive.CreateEntry(PluginStaticSettings.FileName).Open());
            writer.Write(content);
            return path;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string name, string existing, nint security);
}
