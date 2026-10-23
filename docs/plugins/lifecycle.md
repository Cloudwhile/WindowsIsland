# 生命周期与设置

## 调用顺序

| 阶段 | 插件负责的工作 |
| --- | --- |
| 构造函数 | 创建轻量本地状态，不访问宿主服务 |
| `InitializeAsync(context)` | 保存上下文，读取设置，执行幂等迁移 |
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

卸载在删除代码前调用 `OnUninstallAsync`。钩子失败会记录诊断并继续删除；数据目录仍保留。已停用的插件可能被临时构造和初始化以执行卸载，但不会启动监听。

## 读写设置

`context.Settings` 按插件标识隔离，支持后台线程调用：

```csharp
var endpoint = context.Settings.GetString("endpoint", "https://example.com");
var enabled = context.Settings.GetBoolean("enabled", true);
context.Settings.SetString("endpoint", endpoint);
context.Settings.SetBoolean("enabled", enabled);
context.Settings.SetString("obsoleteKey", null);
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

声明在 `InitializeAsync` 前就可能被读取，不能依赖已启动的监听；停用时宿主也可能只构造实例读取声明后释放。设置界面使用数据声明，不支持自定义设置 XAML。

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

数据目录与代码目录分开，卸载代码时保留。`settings` 权限控制此服务，不关闭 `context.Settings`，也不阻止插件直接调用文件 API。

## 更新迁移

新实例初始化时，`context.PreviousVersion` 表示此次安装替换的版本，首次安装为 `null`。用它识别需要迁移的旧数据；`context.HostVersion` 可辅助判断宿主版本，但旧宿主可能返回空字符串。

迁移应能重复执行，保留可恢复的旧数据，以支持初始化重试和安装回滚。更换插件标识会改变设置范围和数据目录，不能只修改清单后假定旧数据自动迁移，见[标识迁移](./packaging.md#迁移旧标识)。
