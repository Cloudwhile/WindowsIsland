namespace WindowsIsland.Contracts;

/// <summary>查询宿主对当前插件授予的代理能力；操作系统隔离由宿主管理。</summary>
public interface IPluginPermissions
{
    /// <summary>判断指定能力是否已由宿主授予。</summary>
    /// <param name="permission">清单中的能力标识。</param>
    /// <returns>已经授予时为真。</returns>
    bool IsGranted(string permission);
}

/// <summary>定义插件能力名称；所有能力均由宿主核验并通过受限服务提供。</summary>
/// <remarks>桌面及进程代理只提供给通过指定来源验证并声明范围的适配器，不授予直接系统访问。未知名称不授予。</remarks>
public static class PluginPermissions
{
    /// <summary>向宿主提交通知。</summary>
    public const string Notifications = "notifications";
    /// <summary>提供插件自己的数据目录；插件偏好通过宿主设置服务保存。</summary>
    public const string Settings = "settings";
    /// <summary>保留旧版二进制兼容性的历史标签，当前宿主不再授予该能力。</summary>
    public const string WeChat = "wechat";
    /// <summary>使用经来源核验的客户端模块代理，并为通知指定经宿主核对的点击目标。</summary>
    public const string Process = "process";
    /// <summary>使用启用时确认联网须知的 IPluginNetwork 服务，不授予直接联网。</summary>
    public const string Network = "network";
    /// <summary>声明插件数据目录之外的文件访问需求。</summary>
    public const string FileSystem = "filesystem";
    /// <summary>声明原生代码访问需求。</summary>
    public const string Native = "native";
    /// <summary>使用经来源及范围核验的 IPluginDesktop 服务，不授予直接桌面访问。</summary>
    public const string Automation = "automation";
    /// <summary>判断能力名称是否由当前合约定义。</summary>
    /// <param name="permission">能力名称。</param>
    /// <returns>属于已定义能力时为真。</returns>
    public static bool IsKnown(string permission) => permission is Notifications or Settings or WeChat or Process or Network or FileSystem or Native or Automation;
    /// <summary>判断已知能力是否需要单独授权。</summary>
    /// <param name="permission">能力名称。</param>
    /// <returns>属于高危能力时为真。</returns>
    public static bool IsHighRisk(string permission) => permission is WeChat or Process or Network or FileSystem or Native or Automation;
}
