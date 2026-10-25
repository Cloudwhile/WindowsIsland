using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

// 仅满足原生 launcher 的输入，不链接管理器或在宿主加载任何插件类型。
internal sealed record ExternalPlugin(PluginManifest Manifest, string HostRoot, string Directory, string DataDirectory);
