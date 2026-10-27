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

```text
用户启用 → 宿主确认授权 → 初始化 → 启动 → 后台工作
                                      ↓
用户停用 → 撤销投递能力 → 通知停止原因 → 取消并等待 → 释放
```

| 状态变化 | 插件处理 |
| --- | --- |
| 启动重复调用 | 复用本轮任务，不重复订阅 |
| 启动失败 | 取消已创建任务，交由宿主显示失败并等待用户重试 |
| 启动后立即停止 | 取消延迟工作，等待任务退出 |
| 停用后重新启用 | 重新建立基线，不投递旧实例缓存 |

带取消、等待与异常诊断的完整模板见[独立示例](./samples.md)，可用 `wini-plugin test` 执行正常运行和立即停止两个场景。

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

### 启用前配置（静态声明）

需要首次启用前配置的插件，可以在 ZIP 根目录放置 `settings.schema.json`。宿主在安装和扫描时验证它，在详情页直接读取数据，不执行插件 DLL；不需要 `settings` 权限或预先同意运行插件。修改配置不会启用插件或授予联网许可。

```json
{
  "schemaVersion": 1,
  "fields": [
    { "key": "endpoint", "type": "string", "title": "服务地址", "default": "https://example.com" },
    { "key": "enabled", "type": "boolean", "title": "接收消息", "default": true }
  ]
}
```

版本 1 只支持 `string` 和 `boolean`，对应现有 `context.Settings`。字符串默认值可省略或为 `null`，布尔项必须提供布尔默认值。默认值用于显示和读取回退，不会仅因打开详情页而写入设置。运行实例仍在合适时机重新读取设置。

文件最多 64 KiB、64 个字段。键最长 128 字符且不重复，不能以 `Host.` 开头；标题为非空文本，最长 200 字符；字符串默认值最长 4096 字符。拒绝重复 JSON 属性、未知版本、未知字段和类型，以及硬链接或重解析点。无效声明会阻止安装；已安装声明损坏时不执行 DLL 作为后备。

静态声明存在时优先使用，空 `fields` 表示不显示配置项；没有该文件时保留动态声明行为。设置按插件标识持久保存，停用后仍可编辑。声明文件属于包摘要，修改后原授权失效。

版本 2 增加整数、枚举、HTTPS URL、宿主加密的密码字段及运行时变更通知，见[静态设置与敏感值](./settings.md)。版本 1 的行为保持兼容，普通字符串不适合保存令牌或密码。

### 运行时配置（动态声明）

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
