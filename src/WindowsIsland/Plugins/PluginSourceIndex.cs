using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

internal sealed record PluginSourceIndex(PluginSource Source, PluginCatalogEntry[] Plugins);
