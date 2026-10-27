# 示例库

[仓库 samples 目录](https://github.com/Cloudwhile/WindowsIsland/tree/master/samples)提供三个独立项目，每个包含源码、清单、静态设置和 README。

| 示例 | 操作与预期 |
| --- | --- |
| `HelloNotification` | 开启一秒后显示通知；立即停用取消延迟任务 |
| `FixedControls` | 滑块设为 3 后点击增加，计数增加 3；关闭开关后按钮不可用 |
| `NetworkStatus` | 默认不联网；配置实际 HTTPS 目的地并开启请求后显示响应状态码 |

先按[SDK 安装说明](./sdk.md)安装 CLI，将独立 Contracts 包放到本地 feed。在任意示例目录执行：

```powershell
dotnet restore --source C:\wini-sdk-feed
wini-plugin validate
wini-plugin pack
```

项目通过 NuGet 引用独立 Contracts，不引用宿主源码。示例当前引用 `0.1.0`；使用其他 SDK 包版本时同步调整 `.csproj` 的 `PackageReference`，并核对目标宿主的合约与能力。

将 `dist` 中的 ZIP 导入消息岛并启用，检查表中的结果，再停用和重新开启。固定内容全局只显示最近提交的一项；运行该示例时，其他来源提交的固定内容可能替换计数器。

联网示例实际运行前，应在源码与清单中同步填写自己的 HTTPS 目的地及用途，并在插件设置中开启 `send`。

需要独立功能测试时，可用 `wini-plugin init` 生成相应模板，参考其 `plugin.test.json` 在示例目录编写场景，再运行 `wini-plugin test`。应覆盖正常运行、立即停止及固定控件交互；修改功能后同步更新预期，具体字段见[独立功能测试](./sdk.md#独立功能测试)。
