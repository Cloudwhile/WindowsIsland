namespace WindowsIsland.Contracts;

/// <summary>描述插件提交的通知数据，不包含界面控件或进程对象。</summary>
/// <remarks>提交后插件不再修改图标和头像字节数组；图像解码、去重和点击处理由宿主执行。</remarks>
/// <param name="SourceId">产生此通知的插件全局唯一标识。</param>
/// <param name="ConversationName">会话名称或通知标题。</param>
/// <param name="Sender">发送者名称，无独立发送者时为空引用。</param>
/// <param name="Body">通知正文。</param>
/// <param name="CreatedAt">通知发生时间及其时区偏移。</param>
/// <param name="AppIcon">应用图标的编码图像字节，缺少时由宿主提供回退。</param>
/// <param name="SenderAvatar">会话或发送者头像的编码图像字节。</param>
/// <param name="DeduplicationKey">来源内稳定的事件去重键；元数据补全沿用原键。</param>
/// <param name="OriginProcessId">点击通知时应激活的目标进程编号。</param>
/// <param name="AppName">第三方应用的显示名称。</param>
/// <param name="AppId">第三方应用的稳定标识，供宿主匹配不同来源的同一应用。</param>
/// <param name="Id">来源内的通知编号。</param>
/// <param name="Symbol">无图像时可选的符号字形文本，由宿主解释和呈现。</param>
/// <param name="IsMetadataUpdate">是否仅补全已有通知的图像等元数据，不代表新消息。</param>
/// <param name="Kind">通知的传递途径，用于保留既有管线的合并优先级。</param>
public sealed record PluginNotification(string SourceId, string ConversationName, string? Sender, string Body,
    DateTimeOffset CreatedAt, byte[]? AppIcon = null, byte[]? SenderAvatar = null, string? DeduplicationKey = null,
    int? OriginProcessId = null, string? AppName = null, string? AppId = null, uint Id = 0, string? Symbol = null,
    bool IsMetadataUpdate = false, PluginNotificationKind Kind = PluginNotificationKind.SystemNotification);
