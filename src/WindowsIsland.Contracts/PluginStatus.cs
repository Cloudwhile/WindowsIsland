namespace WindowsIsland.Contracts;

/// <summary>描述消息来源的连接结果，不包含连接过程状态。</summary>
public enum PluginConnectionState
{
    /// <summary>已连接或已开启。</summary>
    Connected,
    /// <summary>未连接或连接失败，原因由状态附带。</summary>
    Disconnected,
    /// <summary>消息来源客户端未运行。</summary>
    NotRunning
}

/// <summary>描述连接结果的固定原因，由宿主按当前语言呈现。</summary>
public enum PluginStatusReason
{
    /// <summary>没有附加原因。</summary>
    None,
    /// <summary>未检测到客户端。</summary>
    ClientNotDetected,
    /// <summary>需要系统通知授权。</summary>
    PermissionRequired,
    /// <summary>系统通知授权被拒绝。</summary>
    PermissionDenied,
    /// <summary>系统通知初始化未完成。</summary>
    RegistrationRequired,
    /// <summary>来源不可用。</summary>
    Unavailable,
    /// <summary>当前客户端版本不支持直连。</summary>
    VersionNotSupported,
    /// <summary>连接客户端失败。</summary>
    ConnectionFailed
}

/// <summary>描述来源的连接结果和原因。</summary>
/// <param name="State">当前连接结果。</param>
/// <param name="Reason">可选的固定原因。</param>
public sealed record PluginStatus(PluginConnectionState State, PluginStatusReason Reason = PluginStatusReason.None);

/// <summary>提供消息来源的当前结果状态。</summary>
public interface IPluginStatusProvider
{
    /// <summary>获取可从宿主读取的连接结果。</summary>
    PluginStatus Status { get; }
}
