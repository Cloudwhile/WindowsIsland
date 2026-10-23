# 打包、分发与迁移

## ZIP 结构

```text
Example.Notifications-1.0.0.zip
├── manifest.json
├── Example.Notifications.dll
└── Example.Dependency.dll    （仅在需要时提供）
```

根目录必须且只能有一个 `manifest.json`，不要在外面再套插件文件夹。只打包插件自己的程序集、依赖和必要资源；宿主共享 Contracts，不需要把应用运行时放进插件包。

支持的包包含最多 4096 个条目，单文件解压后不超过 128 MiB，总计不超过 256 MiB。不使用路径穿越、重复路径、设备名称、链接或重解析点。

在[入门示例](./getting-started.md)的项目结构下，可使用：

```powershell
$stage = 'artifacts/plugin-example/package'
Compress-Archive -Path "$stage/*" -DestinationPath artifacts/plugin-example/Example.Notifications-1.0.0.zip -Force
Get-FileHash artifacts/plugin-example/Example.Notifications-1.0.0.zip -Algorithm SHA256
```

## 手动分发

向使用者提供 ZIP、版本、支持的消息岛版本、能力声明与使用说明。在 **插件列表** 页导入，不要求使用者手动覆盖应用目录。

同版本重装需要确认覆盖；低版本安装被拒绝。更新保持同一标识和入口身份，涉及设置迁移时，通过 `PreviousVersion` 执行可重复的迁移。

## 官方目录分发

官方目录使用按开发者分组的索引。公开仓库应与开发者索引一致，包上传到该 GitHub 仓库的 Release 附件中，下载地址使用对应版本的 HTTPS ZIP 链接。

目录项应提供以下信息：

| 字段 | 说明 |
| --- | --- |
| `id` / `name` / `version` | 与包内插件对应的身份和版本 |
| `developerId` / `repositoryUrl` | 与所属开发者来源一致 |
| `downloadUrl` | 该仓库公开 Release 的 ZIP 附件地址 |
| `sha256` | ZIP 的 64 位十六进制 SHA-256 摘要 |
| `minHostVersion` | 最低宿主 SemVer 版本 |
| `contractVersion` | 所需合约大版本 |
| `permissions` / `description` | 能力声明与说明 |

收录与审核由目录维护者决定，不会因为清单中声明官方来源而自动生效。没有可用目录服务时，先使用手动 ZIP 分发，不依赖未公布的提交接口。

## 发布前检查

1. 清单标识、入口 `Id`、通知 `SourceId` 一致，符合 `wini_plugin_<name>`。
2. 解压到干净目录后，入口和所有非平台依赖完整。
3. 导入后可开启、停用、重新开启，重复启动没有重复订阅。
4. 一条事件只经过一个提交通道，重新连接不重放历史。
5. 停用后等待后台任务结束，旧消息与旧交互不能恢复显示。
6. 缺少可选服务、手动来源高危权限不可用时仍能合理降级。
7. 更新失败可恢复旧代码与数据，设置迁移可重复执行。
8. 删除只移除插件代码和宿主管理的设置，独立数据目录按预期保留。

开发用实例与日常实例分开，避免覆盖实际插件数据。仓库内的合约和插件回归可以在根目录运行：

```powershell
dotnet run --project tests/WindowsIsland.LogicTests -c Release
```

## 迁移旧标识

旧格式 `wini_<name>_plugin` 已改为 `wini_plugin_<name>`。例如：

```text
wini_example_notifications_plugin → wini_plugin_example_notifications
```

外部插件需要同时修改入口 `Id`、清单、通知 `SourceId`、固定内容 `PluginId` 和测试数据，然后重新编译打包。仅重命名 ZIP 或文件夹不能让旧程序集通过身份校验。

宿主只自动迁移四个内置来源的历史设置范围和诊断标识，保持已有开关；不自动重写外部插件的清单、程序集或数据。外部插件的新标识对应新的设置范围和数据目录，开发者应提供明确的数据导出、导入或迁移步骤，并保留原数据供恢复。

## 合约版本

当前合约大版本为 `1`，仅与相同的正整数大版本兼容。新能力用可选服务查询，旧宿主缺少服务时返回 `null`。

发布需要新合约大版本的插件时，提供破坏性变更、替代 API、适用宿主版本和迁移示例。宿主版本与插件版本分别使用各自的 SemVer，不通过发布时间推断兼容性。
