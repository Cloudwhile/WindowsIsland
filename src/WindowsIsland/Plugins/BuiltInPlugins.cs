using Microsoft.UI.Dispatching;
using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

internal sealed record BuiltInPluginContext(DispatcherQueue Dispatcher, nint Window, bool Verification)
{
    public string? FixtureDirectory(string project) => Verification
        ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "bin", project)) : null;
}

internal static class BuiltInPlugins
{
    public static PluginManager Create(BuiltInPluginContext context, SettingsStore settings) =>
        PluginManager.CreateBuiltIns(context, settings);
}
