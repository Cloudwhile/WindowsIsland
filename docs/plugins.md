# 插件开发

从[创建第一个插件](./plugins/getting-started.md)开始，完成编译、打包和导入。

| 文档 | 内容 |
| --- | --- |
| [创建第一个插件](./plugins/getting-started.md) | 项目、完整入口示例与首次安装 |
| [SDK 与命令行工具](./plugins/sdk.md) | 工具安装、构建、功能测试与打包 |
| [示例库](./plugins/samples.md) | 通知、固定控件和联网的独立项目 |
| [清单与标识](./plugins/manifest.md) | `manifest.json`、`wini_plugin_<name>` 与字段说明 |
| [通知与元数据](./plugins/notifications.md) | DTO、事件、去重、头像补全和点击激活 |
| [生命周期与设置](./plugins/lifecycle.md) | 启停、取消与设置声明 |
| [固定内容与交互](./plugins/pinned-content.md) | 持续显示、详情、控件与交互回调 |
| [权限与诊断](./plugins/permissions.md) | 能力声明、信任边界与结构化诊断 |
| [打包与分发](./plugins/packaging.md) | ZIP 结构、校验与发布检查 |
| [平台目录协议（高级，尚未开放）](./plugins/catalog.md) | 面向目录服务实现者，不是创建首个 ZIP 插件的前置步骤 |

插件只引用 `WindowsIsland.Contracts`，入口实现 `IIslandPlugin`，通知来源可实现
`IMessageSource`。宿主负责调度和窗口，插件经 `IPluginContext` 提交通知及固定内容。

| 需求 | 当前能力 |
| --- | --- |
| 发送通知、提交固定内容和控件 | 声明 `notifications`，使用上下文；已有事件模型可使用 `IMessageSource` |
| 保存字符串和布尔配置 | 使用 `context.Settings`，无需 `settings` 权限 |
| 使用独立数据目录 | 声明 `settings` 并查询 `IPluginStorage` |
| 同步插件自己的数据 | 声明 `network` 和联网须知，用户确认后通过宿主代理发送 |
| 直接操作宿主窗口、读取其他插件数据或提交自定义 XAML | 不开放 |

开发顺序：先运行最小通知示例，再添加设置、固定内容或联网，最后校验清单并打包。异常时从[调试失败](./plugins/permissions.md#调试失败)开始排查。

## 合约与能力发现

合约大版本必须与宿主一致，版本 `1` 的程序集版本固定为 `1.0.0.0`。可选服务通过 `context.GetService<T>()` 查询，未提供时返回 `null`，插件应保留降级路径。入口与依赖见[清单与标识](./plugins/manifest.md#入口与依赖)，宿主及上版插件版本见[版本上下文](./plugins/lifecycle.md#版本上下文)。

## 设置界面

包内 `settings.schema.json` 可声明启用前可编辑的字符串和布尔设置，无需执行 DLL。旧插件仍可使用 `IPluginSettingsSchema` 动态声明，需先授权并启用。字段规则、保存行为和数据目录见[生命周期与设置](./plugins/lifecycle.md)。

## 生命周期

插件按初始化、启动、停止和释放的顺序运行。后台任务必须支持取消，停止后不能继续投递。完整时序、停止原因和卸载钩子见[生命周期](./plugins/lifecycle.md#调用顺序)。

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

首次开发使用手动 ZIP 导入即可。字段和来源规则见[清单与标识](./plugins/manifest.md)，安装和分发步骤见[打包与分发](./plugins/packaging.md)。[平台目录协议](./plugins/catalog.md)供进阶阅读，开放平台目前尚未开放。

## 本地验证

先在独立目录编译和打包，再导入开发用的消息岛实例。检查启停、重新开启、消息投递与停止后回调；具体步骤见[打包指南](./plugins/packaging.md#发布前检查)。
