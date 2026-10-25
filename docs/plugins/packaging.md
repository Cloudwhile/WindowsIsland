# 打包与分发

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

向使用者提供 ZIP、版本、支持的消息岛版本、能力声明与使用说明。在 **插件管理** 页通过安装图标导入，不要求使用者手动覆盖应用目录。

同版本重装需要确认覆盖；低版本安装被拒绝。同一个插件更新时保持标识和入口身份一致。

安装与更新完成后插件保持关闭，用户在消息来源或已安装详情中启用。有条款时在清单内提供完整纯文本；需要联网时提供用途、目的地及数据类别，不能仅在仓库网页中说明。新包和新的披露内容均需重新确认。

## 默认插件源分发

开放平台目前尚未开放。现阶段使用手动 ZIP 分发。

完整端点与字段规则见[目录接口契约](./catalog.md)。官方开发者身份仅认 `winiofficial`；目录中其他开发者仍按三方权限处理。

默认插件源使用按开发者分组的索引。公开仓库应与开发者索引一致，包上传到该 GitHub 仓库的 Release 附件中，下载地址使用对应版本的 HTTPS ZIP 链接。

目录项应提供以下信息：

| 字段 | 说明 |
| --- | --- |
| `id` / `name` / `version` | 与包内插件对应的身份和版本 |
| `developerId` / `repositoryUrl` | 与所属开发者来源一致 |
| `downloadUrl` | 该仓库公开 Release 的 ZIP 附件地址 |
| `sha256` | ZIP 的 64 位小写十六进制 SHA-256 摘要 |
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
6. 缺少可选服务、网络请求被拒绝或取消时仍能合理降级，不自动绕过确认。
7. 更新失败可恢复安装前的代码与数据。
8. 删除移除插件代码和来源项，保留插件设置及独立数据目录；同标识重装可继续读取。

开发用实例与日常实例分开，避免覆盖实际插件数据。仓库内的合约和插件回归可以在根目录运行：

```powershell
dotnet run --project tests/WindowsIsland.LogicTests -c Release
```

## 合约版本

当前合约大版本为 `1`，仅与相同的正整数大版本兼容。新能力用可选服务查询，宿主缺少服务时返回 `null`。

发布插件时注明适用的宿主版本和合约大版本。宿主版本与插件版本分别使用各自的 SemVer，不通过发布时间推断兼容性。
