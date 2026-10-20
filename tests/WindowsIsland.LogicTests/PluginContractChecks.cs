using System.Runtime.CompilerServices;
using System.Text.Json;
using WindowsIsland.Contracts;

namespace WindowsIsland.LogicTests;

internal static class PluginContractChecks
{
    // 在不修改现有顶级测试入口的前提下接入检查，带参数的子进程夹具保持原有入口行为。
    [ModuleInitializer]
    internal static void Run()
    {
        if (Environment.GetCommandLineArgs().Length != 1) return;
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks++;
            Console.WriteLine($"PASS {name}");
        }

        var manifest = new PluginManifest("example.wechat", "微信消息来源", "1.2.3-alpha.1", "Example.WeChat.dll",
            "Example.WeChat.MessageSource", PluginContract.Version, ["notifications", "settings"], "示例作者", "会话监听与头像补全");
        foreach (var options in new[] { new JsonSerializerOptions(), new JsonSerializerOptions(JsonSerializerDefaults.Web) })
        {
            var json = JsonSerializer.Serialize(manifest, options);
            var restored = JsonSerializer.Deserialize<PluginManifest>(json, options)!;
            Check(restored is not null && restored with { Permissions = manifest.Permissions } == manifest
                && restored.Permissions.SequenceEqual(manifest.Permissions),
                "Plugin manifest round-trips every field, Chinese text and permissions with default and web JSON options");
        }
        var minimal = manifest with { Permissions = [], Author = null, Description = null };
        var restoredMinimal = JsonSerializer.Deserialize<PluginManifest>(JsonSerializer.Serialize(minimal))!;
        Check(restoredMinimal.Permissions.Length == 0 && restoredMinimal.Author is null && restoredMinimal.Description is null
            && restoredMinimal with { Permissions = minimal.Permissions } == minimal,
            "Plugin manifest round-trips empty permissions and absent optional metadata");
        var external = JsonSerializer.Deserialize<PluginManifest>("""
            {"Id":"example.external","Name":"外部来源","Version":"2.0.0","EntryAssembly":"External.dll",
             "EntryType":"External.Source","ContractVersion":1,"Permissions":["notifications"]}
            """)!;
        Check(external.Id == "example.external" && external.ContractVersion == PluginContract.Version
            && external.Author is null && external.Description is null && external.Permissions.SequenceEqual(["notifications"]),
            "Plugin manifests authored outside the host deserialize without optional metadata");

        Check(PluginContract.Version == 1 && PluginContract.IsCompatible(1), "Current plugin contract is version one and compatible");
        foreach (var version in new[] { int.MinValue, -1, 0, 2, int.MaxValue })
            Check(!PluginContract.IsCompatible(version), $"Plugin contract version {version} is incompatible with the current host");
        Check(PluginContract.IsCompatible(2, 2), "Matching explicit host and plugin contract major versions are compatible");
        Check(!PluginContract.IsCompatible(1, 2) && !PluginContract.IsCompatible(2, 1),
            "Different host and plugin contract major versions are incompatible in both directions");
        Check(!PluginContract.IsCompatible(0, 0) && !PluginContract.IsCompatible(-1, -1)
            && !PluginContract.IsCompatible(1, 0) && !PluginContract.IsCompatible(1, -1),
            "Invalid major versions are incompatible even when equal");

        var notification = new PluginNotification("example.wechat", "测试会话", "发送者", "新消息",
            DateTimeOffset.Parse("2026-10-20T08:30:00+08:00"), AppIcon: [1, 2], SenderAvatar: [3, 4],
            DeduplicationKey: "conversation/event-1", OriginProcessId: 1234, AppName: "WeChat", AppId: "wechat", Id: 7,
            Symbol: "消息", IsMetadataUpdate: true);
        var restoredNotification = JsonSerializer.Deserialize<PluginNotification>(JsonSerializer.Serialize(notification))!;
        Check(restoredNotification with { AppIcon = notification.AppIcon, SenderAvatar = notification.SenderAvatar } == notification
            && restoredNotification.AppIcon!.SequenceEqual(notification.AppIcon!)
            && restoredNotification.SenderAvatar!.SequenceEqual(notification.SenderAvatar!),
            "Independent notification DTO preserves identity, sender, images, click target and metadata-only updates");
        var references = typeof(IIslandPlugin).Assembly.GetReferencedAssemblies();
        Check(references.All(reference => reference.Name is "System" or "netstandard" || reference.Name!.StartsWith("System.", StringComparison.Ordinal)),
            "Plugin contracts reference only base class library assemblies");
        Console.WriteLine($"{checks} plugin contract checks passed.");
    }
}
