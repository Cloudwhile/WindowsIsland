using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

/// <summary>保存宿主注册的插件信息，不要求创建或启动插件实例。</summary>
/// <param name="IsBuiltIn">是否为随应用提供且不可删除的内置插件。</param>
/// <param name="Version">插件的完整版本。</param>
/// <param name="Location">插件所属程序集的文件位置。</param>
/// <param name="Author">可选的插件作者。</param>
internal sealed record PluginMetadata(bool IsBuiltIn, string Version, string Location, string? Author = null, ExternalPlugin? External = null)
{
    /// <summary>获取当前应用内置插件共用的程序集信息。</summary>
    public static PluginMetadata BuiltIn { get; } = new(true, ReleaseVersion.Current.Tag,
        typeof(PluginMetadata).Assembly.Location, "Cloudwhile");

    /// <summary>在后台读取程序集大小，无法读取时返回空值。</summary>
    public Task<long?> ReadSizeAsync() => Task.Run<long?>(() =>
    {
        try { return new FileInfo(Location).Length; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return null; }
    });
}
