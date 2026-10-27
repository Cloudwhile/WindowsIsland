namespace WindowsIsland.Contracts;

/// <summary>通过宿主访问清单声明且经来源验证的桌面客户端；不向插件传递原生句柄。</summary>
public interface IPluginDesktop
{
    /// <summary>枚举允许的客户端及其模块、窗口。</summary>
    Task<DesktopClient[]> FindClientsAsync(CancellationToken cancellationToken = default);
    /// <summary>读取指定客户端窗口的有界可访问性快照。</summary>
    Task<DesktopTree> ReadTreeAsync(int processId, long window, CancellationToken cancellationToken = default);
    /// <summary>读取已声明模块文件的一段内容。</summary>
    Task<byte[]> ReadModuleAsync(DesktopRead request, CancellationToken cancellationToken = default);
    /// <summary>读取已声明模块在客户端内存中的一段内容。</summary>
    Task<byte[]> ReadMemoryAsync(DesktopRead request, CancellationToken cancellationToken = default);
    /// <summary>核对加载模块锚点后设置单字节布尔标记；宿主在撤销时恢复原值。</summary>
    Task<bool> SetFlagAsync(DesktopFlag request, CancellationToken cancellationToken = default);
    /// <summary>读取快照元素提供的本地图像，或捕获该元素内的指定区域。</summary>
    Task<byte[]?> ReadImageAsync(DesktopImage request, CancellationToken cancellationToken = default);
}

/// <summary>声明允许访问的进程名、模块名及用途；此声明不自行授予能力。</summary>
/// <param name="Processes">不带扩展名的客户端进程名。</param>
/// <param name="Modules">允许读取的模块文件名。</param>
/// <param name="Purpose">供使用者核对的访问用途。</param>
public sealed record PluginDesktopScope(string[] Processes, string[] Modules, string Purpose);

/// <summary>描述宿主核验的客户端。</summary>
/// <param name="ProcessId">客户端进程标识。</param><param name="StartedAt">进程创建时间。</param>
/// <param name="Executable">可执行文件路径。</param><param name="Version">客户端文件版本。</param>
/// <param name="Modules">允许访问的模块。</param><param name="Windows">属于客户端的窗口。</param>
/// <param name="Icon">可用的客户端图标 PNG。</param>
public sealed record DesktopClient(int ProcessId, long StartedAt, string Executable, string Version,
    DesktopModule[] Modules, DesktopWindow[] Windows, byte[]? Icon = null);

/// <summary>描述允许访问的模块。</summary>
/// <param name="Name">模块名称。</param><param name="ImageBase">加载地址，仅供验证模块元数据。</param>
/// <param name="ImageSize">加载映像大小。</param><param name="FileLength">模块文件大小。</param>
public sealed record DesktopModule(string Name, long ImageBase, int ImageSize, long FileLength);

/// <summary>描述客户端窗口，不授予可继承句柄。</summary>
/// <param name="Handle">窗口标识，每次使用时重新核对归属。</param><param name="ClassName">窗口类名。</param>
/// <param name="Bounds">屏幕坐标。</param><param name="Style">窗口样式。</param><param name="ExtendedStyle">扩展样式。</param>
/// <param name="Owned">是否有所有者窗口。</param><param name="Visible">是否可见。</param>
/// <param name="Minimized">是否最小化。</param><param name="Foreground">是否前台窗口。</param><param name="Scale">坐标缩放。</param>
public sealed record DesktopWindow(long Handle, string ClassName, DesktopRect Bounds, long Style, long ExtendedStyle,
    bool Owned, bool Visible, bool Minimized, bool Foreground, double Scale);

/// <summary>表示屏幕坐标矩形。</summary>
/// <param name="X">横坐标。</param><param name="Y">纵坐标。</param><param name="Width">宽度。</param><param name="Height">高度。</param>
public readonly record struct DesktopRect(double X, double Y, double Width, double Height)
{
    /// <summary>是否为空矩形。</summary>
    public bool IsEmpty => !double.IsFinite(X + Y + Width + Height) || Width <= 0 || Height <= 0;
    /// <summary>右边界。</summary>
    public double Right => X + Width;
    /// <summary>下边界。</summary>
    public double Bottom => Y + Height;
    /// <summary>是否完整包含指定矩形。</summary>
    public bool Contains(DesktopRect other) => !IsEmpty && !other.IsEmpty && other.X >= X && other.Y >= Y
        && other.Right <= Right && other.Bottom <= Bottom;
}

/// <summary>描述可访问性树节点，Parent 为父节点下标，根节点使用 -1。</summary>
/// <param name="Parent">父节点下标。</param><param name="Name">元素名称。</param><param name="AutomationId">自动化标识。</param>
/// <param name="ClassName">元素类名。</param><param name="ControlType">UI Automation 控件类型标识。</param>
/// <param name="ItemStatus">项目状态。</param><param name="ItemType">项目类型。</param><param name="RuntimeId">运行时身份。</param>
/// <param name="Bounds">屏幕坐标。</param><param name="Offscreen">是否不在可见区域。</param>
public sealed record DesktopNode(int Parent, string Name, string AutomationId, string ClassName, int ControlType,
    string ItemStatus, string ItemType, string RuntimeId, DesktopRect Bounds, bool Offscreen);

/// <summary>描述有时限的可访问性快照。</summary>
/// <param name="Token">仅当前服务实例可使用的快照标识。</param><param name="Nodes">按父节点先于子节点排序的元素。</param>
public sealed record DesktopTree(string Token, DesktopNode[] Nodes);

/// <summary>请求读取客户端模块内的有界区间。</summary>
/// <param name="ProcessId">客户端进程标识。</param><param name="Module">清单声明的模块名。</param>
/// <param name="Offset">文件偏移或模块内存相对偏移。</param><param name="Count">读取字节数。</param>
public sealed record DesktopRead(int ProcessId, string Module, long Offset, int Count);

/// <summary>描述设置布尔标记前必须匹配的内存片段。</summary>
/// <param name="Offset">模块内相对偏移。</param><param name="Bytes">必须逐字匹配的内容。</param>
public sealed record DesktopAnchor(long Offset, byte[] Bytes);

/// <summary>请求设置已核验客户端模块中的单字节布尔标记。</summary>
/// <param name="ProcessId">客户端进程标识。</param><param name="Module">模块名。</param>
/// <param name="Offset">模块内相对偏移。</param><param name="Value">布尔值 0 或 1。</param>
/// <param name="Anchors">写入前核对的模块片段。</param>
public sealed record DesktopFlag(int ProcessId, string Module, long Offset, byte Value, DesktopAnchor[] Anchors);

/// <summary>请求读取快照元素的图像。</summary>
/// <param name="Token">仍有效的快照标识。</param><param name="Node">元素下标。</param>
/// <param name="Bounds">可选的元素内部捕获区域；省略时只读取元素提供的本地图像。</param>
public sealed record DesktopImage(string Token, int Node, DesktopRect? Bounds = null);

/// <summary>提供有界的会话免打扰快照，不接收其他来源的消息正文。</summary>
public interface IPluginConversationPolicy
{
    /// <summary>获取当前可确认的会话状态；宿主不会无限期使用此快照。</summary>
    IReadOnlyList<PluginConversationRule> ConversationRules { get; }
}

/// <summary>描述客户端会话的免打扰状态。</summary>
/// <param name="AppId">客户端身份。</param><param name="ProcessId">可选的客户端进程标识。</param>
/// <param name="Conversation">会话名称。</param><param name="Muted">是否免打扰。</param>
public sealed record PluginConversationRule(string AppId, int? ProcessId, string Conversation, bool Muted);
