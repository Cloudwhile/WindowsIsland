namespace WindowsIsland.Contracts;

/// <summary>查询宿主对当前插件授予的能力；不代表操作系统沙箱。</summary>
public interface IPluginPermissions
{
    /// <summary>判断指定能力是否已由宿主授予。</summary>
    /// <param name="permission">清单中的能力标识。</param>
    /// <returns>已经授予时为真。</returns>
    bool IsGranted(string permission);
}

/// <summary>定义宿主识别的插件能力名称，未知名称始终不授予。</summary>
public static class PluginPermissions
{
    /// <summary>向宿主提交通知。</summary>
    public const string Notifications = "notifications";
    /// <summary>访问插件自身的设置和数据。</summary>
    public const string Settings = "settings";
    /// <summary>为通知指定点击时激活的进程。</summary>
    public const string Process = "process";
    /// <summary>声明网络访问需求。</summary>
    public const string Network = "network";
    /// <summary>声明插件数据目录之外的文件访问需求。</summary>
    public const string FileSystem = "filesystem";
    /// <summary>声明原生代码访问需求。</summary>
    public const string Native = "native";
    /// <summary>声明辅助功能访问需求。</summary>
    public const string Automation = "automation";
    /// <summary>判断能力名称是否由当前合约定义。</summary>
    /// <param name="permission">能力名称。</param>
    /// <returns>属于已定义能力时为真。</returns>
    public static bool IsKnown(string permission) => permission is Notifications or Settings or Process or Network or FileSystem or Native or Automation;
    /// <summary>判断已知能力是否需要单独授权。</summary>
    /// <param name="permission">能力名称。</param>
    /// <returns>属于高危能力时为真。</returns>
    public static bool IsHighRisk(string permission) => permission is Process or Network or FileSystem or Native or Automation;
}
