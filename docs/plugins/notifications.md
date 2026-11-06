# 通知与元数据

## 选择一个提交通道

实现 `IIslandPlugin` 时，可以从任意线程调用上下文：

```csharp
context.PublishNotification(new PluginNotification(
    SourceId: context.PluginId,
    ConversationName: "构建任务",
    Sender: "构建服务",
    Body: "任务已完成",
    CreatedAt: DateTimeOffset.UtcNow,
    DeduplicationKey: "build/job-42",
    AppName: "构建工具",
    AppId: "example.build"));
```

也可以实现 `IMessageSource`，触发 `NotificationReceived` 事件。一次通知只选择事件或上下文中的一个通道，避免重复投递。宿主负责入队、去重和界面呈现。

## 常用字段

| 字段 | 含义 |
| --- | --- |
| `SourceId` | 当前插件标识，必须等于 `context.PluginId` |
| `ConversationName` | 会话名称或标题 |
| `Sender` | 可选的发送者，无独立发送者时为 `null` |
| `Body` | 消息正文 |
| `CreatedAt` | 带时区偏移的事件发生时间 |
| `DeduplicationKey` | 来源内稳定的事件身份 |
| `AppIcon` / `SenderAvatar` | 编码图像的字节数组，不是文件路径或原始像素 |
| `OriginProcessId` | 可选的点击激活目标，需要宿主授予 `process` |
| `AppName` / `AppId` | 应用显示名称与稳定应用标识 |
| `Id` | 来源内可选的无符号数值编号 |
| `Symbol` | 缺少图像时的可选符号 |
| `IsMetadataUpdate` | 仅补全已有通知时设为 `true` |
| `Kind` | 传递途径；普通外部通知可保留默认值 |

提交后不要继续修改图像字节数组；需要新图像时提交新快照。

## 去重与补全

同一事件保持相同的去重键，不同事件使用不同的键。补头像时沿用事件原有的标题、正文、时间与身份：

```csharp
var notification = new PluginNotification(
    context.PluginId, "工作群", "同事", "会议即将开始", DateTimeOffset.UtcNow,
    DeduplicationKey: "conversation-17/message-89");
context.PublishNotification(notification);

// avatarBytes 是稍后取得的编码图像。
context.PublishNotification(notification with
{
    SenderAvatar = avatarBytes,
    IsMetadataUpdate = true
});
```

元数据补全不会重新开始五秒计时，也不应把已结束的消息当作新消息恢复。普通新消息替换当前消息后，会重新计算显示时间。

## 点击激活

`OriginProcessId` 是可选的点击激活信息，但外部插件不获得 `process` 能力，宿主会移除其目标进程号。不要依赖点击通知启动或激活其他进程；普通消息仍可显示。详见[权限说明](./permissions.md)。

## 连接与停止

初始化时准备状态，启动时建立已有事件的基线，只推送之后的新事件。重新连接不要重放整个历史列表。

宿主在停用时撤销旧上下文，迟到通知会被丢弃。插件仍需主动取消工作、解除事件并等待停止，避免后台任务和外部资源泄漏。
