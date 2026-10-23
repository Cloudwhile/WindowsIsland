# 固定内容与交互

固定内容让插件持续显示一个简短值，并可提供展开详情。消息岛统一决定尺寸、显隐、焦点和消息优先级。

## 查询能力并提交

```csharp
var pinned = context.PinnedContent;
if (pinned is not null)
{
    pinned.Submit(new PinnedContent(
        Id: "task-status",
        PluginId: context.PluginId,
        Title: "任务状态",
        CompactText: "3 项待办",
        Body: "还有 3 项任务待处理。"));
}
```

旧宿主可能不提供固定内容能力。插件应处理 `null`，保留普通通知等可用行为。

`Id` 是当前插件内部稳定的内容标识，`PluginId` 与上下文相同。全局只显示最近提交的一项固定内容。

## 更新与撤下

```csharp
pinned?.Update(new PinnedContent(
    "task-status", context.PluginId, "任务状态", "2 项待办",
    Body: "还有 2 项任务待处理。"));

// 任务结束时由插件主动撤下。
pinned?.Remove("task-status");
```

`Update` 提交完整快照，只更新仍然有效的同标识内容，不会恢复已撤下或被其他内容替换的项目。更新当前值不会重新播放展开动画。

固定内容没有五秒到期限制，由插件主动撤下；插件停用、失败或卸载时宿主也会移除。需要周期更新时，由插件管理计时器并在停止时释放。

## 添加数据控件

支持按钮、开关和滑块。先注册交互处理器，再提交含控件的快照：

```csharp
var pinned = context.PinnedContent;
if (pinned is null) return;

var enabled = context.Settings.GetBoolean("enabled", true);
var snapshot = new PinnedContent(
    "controls", context.PluginId, "接收状态", enabled ? "已开启" : "已关闭",
    Controls:
    [
        new PinnedControl("enabled", "接收消息", PinnedControlKind.Toggle, IsOn: enabled),
        new PinnedControl("volume", "音量", PinnedControlKind.Slider,
            Minimum: 0, Maximum: 100, Value: 50, Step: 1),
        new PinnedControl("refresh", "刷新", PinnedControlKind.Button, Symbol: "Refresh")
    ]);

pinned.SetInteractionHandler(interaction =>
{
    if (interaction.ContentId == "controls"
        && interaction.ControlId == "enabled" && interaction.IsOn is { } value)
    {
        context.Settings.SetBoolean("enabled", value);
        snapshot = snapshot with
        {
            CompactText = value ? "已开启" : "已关闭",
            Controls = snapshot.Controls!.Select(control => control.Id == "enabled"
                ? control with { IsOn = value } : control).ToArray()
        };
        pinned.Update(snapshot);
    }
    return Task.CompletedTask;
});
pinned.Submit(snapshot);
```

示例仅处理开关；插件需要为自己的按钮和滑块添加业务处理。交互返回的是用户请求，完成后提交新的快照来确认最终显示值。

回调在后台按插件串行执行。宿主检查当前实例、内容、控件是否可用，以及滑块范围和步长；回调异常只影响当前插件。停止时取消注册并释放插件自己的资源，迟到的旧回调不能恢复内容。

## 内容范围

| 项目 | 范围 |
| --- | --- |
| 内容 `Id` | 非空白，最多 128 字符 |
| `Title` / `CompactText` | 最多 256 字符 |
| `Body` | 最多 8192 字符 |
| 控件数量 | 最多 32 个 |
| 控件标识 | 非空白、同一内容内唯一，最多 128 字符 |
| 控件标签 | 最多 256 字符 |
| 滑块 | 有限数值、最大值大于最小值、正步长、当前值在范围内 |
| `CustomPanelXaml` | 最多 32768 字符，且仅对允许的来源生效 |

按钮尽量提供 `Symbol`，同时保留有意义的标签作为辅助功能名称。宿主不识别图标时会使用回退。

## 自定义面板

`CustomPanelXaml` 仅适用于内置或官方目录确认的来源。手动插件的 XAML 会被剔除并产生诊断，普通内容、数据控件和消息仍可继续使用。

面板中的原生交互控件使用与数据控件标识一致的 `x:Name`。外部插件应优先使用上述数据控件，不自行创建窗口或更改宿主尺寸、焦点和显示状态。

## 展开与消息优先级

有正文、控件或获准自定义面板时可以展开详情。Esc、点外和关闭图标返回紧凑显示。

普通消息优先，打断固定详情后不会自动重开。游戏或全屏静音会隐藏显示并丢弃消息，固定内容保留，结束静音后回到紧凑态。
