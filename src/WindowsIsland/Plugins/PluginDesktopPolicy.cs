using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

// Only this source-verified adapter has been approved for desktop mediation.
internal static class PluginDesktopPolicy
{
    public static bool IsAllowed(PluginManifest manifest) => manifest.SourceKind == PluginSourceKind.Official
        && MatchesPublisher(manifest.Id, manifest.DeveloperId, manifest.RepositoryUrl)
        && manifest.Desktop is not null && manifest.Permissions.Contains(PluginPermissions.Automation)
        && manifest.Permissions.Contains(PluginPermissions.Process) && manifest.Permissions.Contains(PluginPermissions.Notifications);

    public static bool MatchesPublisher(string id, string developerId, string? repository) => id is "wini_plugin_wechat" or "wini_plugin_telegram"
        && developerId == "winiofficial" && string.Equals(repository,
            "https://github.com/WinIOfficial/" + id, StringComparison.OrdinalIgnoreCase);

    public static void Validate(PluginManifest manifest)
    {
        if (manifest.Desktop is not { } scope) return;
        if (!manifest.Permissions.Contains(PluginPermissions.Automation) || !manifest.Permissions.Contains(PluginPermissions.Process)
            || scope.Processes is not { Length: > 0 and <= 8 } || scope.Modules is not { Length: > 0 and <= 8 }
            || string.IsNullOrWhiteSpace(scope.Purpose) || scope.Purpose.Length > 1000
            || scope.Purpose.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))
            || scope.Processes.Any(name => !Name(name, module: false)) || scope.Modules.Any(name => !Name(name, module: true))
            || scope.Processes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != scope.Processes.Length
            || scope.Modules.Distinct(StringComparer.OrdinalIgnoreCase).Count() != scope.Modules.Length)
            throw new PluginException(PluginErrorCode.Permissions, "插件桌面访问声明无效。");
    }

    private static bool Name(string? value, bool module) => value is { Length: > 0 and <= 80 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' || module && c == '.')
        && (!module || value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) && !value.Contains("..");
}

internal interface IPluginDesktopProcessScope
{
    bool OwnsProcess(int processId);
}
