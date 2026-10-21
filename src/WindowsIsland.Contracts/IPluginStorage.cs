namespace WindowsIsland.Contracts;

/// <summary>提供与代码目录分离的数据目录，卸载代码时保留。</summary>
public interface IPluginStorage
{
    /// <summary>获取当前插件的数据目录。</summary>
    string DataDirectory { get; }
}
