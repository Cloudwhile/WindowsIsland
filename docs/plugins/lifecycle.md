# 生命周期与设置

## 调用顺序

| 阶段 | 插件负责的工作 |
| --- | --- |
| 构造函数 | 创建轻量本地状态，不访问宿主服务 |
| `InitializeAsync(context)` | 保存上下文，读取设置，准备资源 |
| `StartAsync()` | 建立监听基线和后台订阅，完成启动后返回 |
| `OnStoppingAsync(reason)` | 可选，了解停止原因 |
| `StopAsync()` | 取消后台任务、等待退出、释放本轮订阅 |
| `Dispose()` | 释放实例剩余资源 |

不要让 `StartAsync` 一直等待监听循环结束。保存后台任务，并在 `StopAsync` 中取消和等待；重复启动不能增加重复订阅，重复停止应安全。

宿主停用时会先撤销通知与固定内容投递能力，再等待清理。因此，清理所需的资源应提前保存，不依赖停止后的上下文重新申请服务。

初始化、启动或宿主回调中的异常会影响当前插件，用户可以通过来源行重试。插件自行启动的后台任务仍需要自己的异常处理和取消逻辑。

## 可选生命周期钩子

实现 `IPluginLifecycleAware` 可接收以下停止原因：

| `PluginStopReason` | 场景 |
| --- | --- |
| `Disabled` | 用户停用 |
| `Removed` | 用户删除 |
| `Updating` | 新版本替换旧实例 |
| `Shutdown` | 宿主退出 |
| `Failed` | 插件发生异常 |

已运行插件卸载时在删除代码前调用 `OnUninstallAsync`。钩子失败会记录诊断并继续删除；插件设置与数据目录保留，同标识重装可继续读取。未启用或尚未通过授权的外部插件不会为卸载而创建、初始化实例。

## 读写设置

`context.Settings` 按插件标识隔离，支持后台线程调用：

```csharp
var endpoint = context.Settings.GetString("endpoint", "https://example.com");
var enabled = context.Settings.GetBoolean("enabled", true);
context.Settings.SetString("endpoint", endpoint);
context.Settings.SetBoolean("enabled", enabled);
context.Settings.SetString("optionalLabel", null);
```

键为非空白字符串，最长 128 字符。只支持字符串和布尔值；读取不存在或类型损坏的字段会返回指定默认值，写入字符串 `null` 会删除该键。

## 声明设置界面

入口实现 `IPluginSettingsSchema`：

```csharp
public IReadOnlyList<PluginSetting> SettingsSchema =>
[
    new("endpoint", PluginSettingType.String, "服务地址", "https://example.com"),
    new("enabled", PluginSettingType.Boolean, "接收消息", true)
];
```

同一个声明中键不能重复，默认值类型与设置类型一致；字符串默认值可以为 `null`，布尔项应给出布尔默认值。

宿主在来源详情里呈现文本框和开关，修改立即保存，不会因此重新启动插件。运行实例应在合适的时机重新读取设置，并自行处理输入校验。

声明不能依赖已启动的监听。首次读取外部插件的动态设置前，需先完成授权并启用；停用时不会为了读取声明执行插件代码。设置界面使用数据声明，不支持自定义设置 XAML。

## 数据目录

声明并获授 `settings` 后，可以查询宿主数据目录服务：

```csharp
var storage = context.GetService<IPluginStorage>();
if (storage is not null)
{
    var file = Path.Combine(storage.DataDirectory, "state.json");
    // 在后台按需读写插件自己的状态文件。
}
```

数据目录与代码目录分开，卸载代码时保留。`settings` 权限控制此服务，不关闭 `context.Settings`。外部插件仅能访问自身数据，不得读取消息岛或其他插件的数据目录。

## 版本上下文

`context.HostVersion` 提供宿主的完整版本字符串，`context.PreviousVersion` 表示此次安装替换的插件版本，首次安装为 `null`。
