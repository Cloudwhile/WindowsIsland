# 插件开发

从[创建第一个插件](./plugins/getting-started.md)开始，完成编译、打包和导入。

| 文档 | 内容 |
| --- | --- |
| [创建第一个插件](./plugins/getting-started.md) | 项目、完整入口示例与首次安装 |
| [清单与标识](./plugins/manifest.md) | `manifest.json`、`wini_plugin_<name>` 与字段说明 |
| [通知与元数据](./plugins/notifications.md) | DTO、事件、去重、头像补全和点击激活 |
| [生命周期与设置](./plugins/lifecycle.md) | 启停、取消与设置声明 |
| [固定内容与交互](./plugins/pinned-content.md) | 持续显示、详情、控件与交互回调 |
| [权限与诊断](./plugins/permissions.md) | 能力声明、信任边界与结构化诊断 |
| [目录接口契约](./plugins/catalog.md) | 目录端点、字段、官方身份、更新与下架语义 |
| [打包与分发](./plugins/packaging.md) | ZIP 结构、校验与发布检查 |

插件只引用 `WindowsIsland.Contracts`，入口实现 `IIslandPlugin`，通知来源可实现
`IMessageSource`。宿主负责调度和窗口，插件经 `IPluginContext` 提交通知及固定内容。

## 合约与能力发现

`PluginContract.IsCompatible` 只接受相同且为正整数的合约大版本。一个大版本内
不得向现有接口增加必填方法。新的宿主能力通过 `context.GetService<T>()` 查询，
未提供时返回 `null`，插件应保留无此能力时的降级路径；固定内容服务是现有范例。
插件侧扩展采用可选接口，按所需能力实现。

合约大版本 `1` 的程序集版本固定为 `1.0.0.0`，不随宿主版本改变。插件版本、
宿主版本和合约版本分别管理；引用对应的合约 DLL，并使用可选服务发现新增能力。

`context.HostVersion` 是宿主的完整版本字符串。
`context.PreviousVersion` 是安装操作替换的插件版本，首次安装为 `null`。
这两个属性均提供默认实现，不要求插件实现宿主上下文，也不改变合约大版本。

## 设置界面

入口可实现 `IPluginSettingsSchema`，用 `SettingsSchema` 返回 `PluginSetting` 数据。
仅支持 `PluginSettingType.String` 和 `Boolean`，与 `IPluginSettings` 一一对应：

```csharp
public IReadOnlyList<PluginSetting> SettingsSchema =>
[
    new("endpoint", PluginSettingType.String, "服务地址", "https://example.com"),
    new("enabled", PluginSettingType.Boolean, "接收消息", true)
];
```

键必须非空白且不超过 128 字符，同一 schema 不得重复。默认值必须是对应的
string（可为 null）或 bool。标题、默认值和业务校验归插件负责，宿主不把
这些内容写进清单。schema 在初始化前即可读取，读取不能依赖监听已启动。
点击插件进入的详情页使用原生文本框和开关，变更立即写入
`IPluginSettings`，运行实例读同一存储，不因编辑重启。无设置页自定义 XAML。
外部插件首次提供动态设置前需要先完成授权并启用，停用时不执行插件代码读取 schema。

## 生命周期

入口可实现 `IPluginLifecycleAware`。宿主先调用 `OnStoppingAsync(reason)`，
再调用 `StopAsync` 和 `Dispose`。原因包括 `Disabled`、`Removed`、`Updating`、
`Shutdown`、`Failed`。停用先撤销上下文投递及旧代次写入能力，再异步清理；
插件应在初始化时保留清理所需的资源，不依赖停止后的上下文重新申请服务。

卸载在删除代码前调用可选的 `OnUninstallAsync`。钩子失败记本机诊断，继续删除；
插件设置与数据目录保留，同标识重装可继续读取。已停用或未授权的外部插件不会为卸载而创建清理实例。
更新时当前实例收到 `Updating`，替换后的实例通过 `PreviousVersion` 取得此次安装替换的版本。

## 权限与启用确认

外部插件安装后默认关闭。用户启用前核对身份和版本，有条款时阅读并明确同意；
需要联网时另行核对网络目的地、用途和数据类别。更新或同版本覆盖后需重新确认。
官方作者和第三方作者均遵循此流程，后台启动与重试不能代替用户同意。

外部插件只能使用自身数据，不能读取消息岛本体或其他插件的数据。
`notifications` 控制通知投递，`settings` 提供自身数据目录服务；
`network` 提供宿主授权的 `IPluginNetwork`。启用时确认开发者或默认插件源提供的联网须知，之后可在声明范围内后台联网。
其他系统访问能力不向外部插件开放；可选服务缺失或请求被拒绝时应保留合理降级。
完整说明与示例见[权限与诊断](./plugins/permissions.md)。

## 清单与目录发现

清单 `Description` 最多 8000 字符，`Author` 最多 200 字符，与默认插件源项一致。
安装和扫描均校验清单与 PE 元数据，预校验不执行插件入口代码。
设置页手动刷新重新扫描 `Plugins/official` 和 `Plugins/community` 目录，只增加有效的新目录；坏目录独立
记录诊断。删除标记不会因扫描清除，目录缺失也不会自动删除已注册项。
重新安装仍走原安装确认及权限流程，可以显式清除删除标记。

## 本地验证

先在独立目录编译和打包，再导入开发用的消息岛实例。检查启停、重新开启、消息投递与停止后回调；具体步骤见[打包指南](./plugins/packaging.md#发布前检查)。
