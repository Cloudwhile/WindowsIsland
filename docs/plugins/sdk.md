# SDK 与命令行工具

`WindowsIsland.Plugin.Cli` 提供 `wini-plugin` 命令。需要 Windows 和 .NET 10 SDK。工具包包含与本版本宿主匹配的 Contracts，生成的项目不引用宿主主程序集或 WinUI，也不需要克隆宿主仓库。

## 安装工具包

工具通过构建产出的 `.nupkg` 分发，尚未发布到公共 NuGet 源。可按下文从源码构建 CLI 和独立 Contracts 包，或取得与目标宿主同次构建的工具包。将包放入 `C:\wini-sdk-feed`，执行：

```powershell
dotnet tool install WindowsIsland.Plugin.Cli --tool-path C:\wini-tools --add-source C:\wini-sdk-feed
C:\wini-tools\wini-plugin --help
```

可以将 `C:\wini-tools` 加入自己的 PATH；下文使用该目录的完整路径。

维护者可以在源码中构建工具包：

```powershell
dotnet pack src/WindowsIsland.Plugin.Cli/WindowsIsland.Plugin.Cli.csproj -c Release -o artifacts/sdk-feed
dotnet pack src/WindowsIsland.Contracts/WindowsIsland.Contracts.csproj -c Release -o artifacts/sdk-feed
```

## 从空目录生成插件

```powershell
C:\wini-tools\wini-plugin init HelloIsland --template notification
cd HelloIsland
C:\wini-tools\wini-plugin validate
C:\wini-tools\wini-plugin test
C:\wini-tools\wini-plugin pack
```

从消息岛的插件管理导入 `dist/wini_plugin_helloisland-1.0.0.zip`，启用 **HelloIsland** 后约一秒显示示例通知。停用后重新启用可再次验证；立即停用应取消旧任务。

生成项目包含 `src/Plugin.cs`、清单、静态设置、`plugin.test.json`、独立合约 DLL 和 README。静态设置可在插件关闭时修改。Contracts 的 `AssemblyVersion` 固定为 `1.0.0.0`，清单的 `contractVersion` 为 `1`；升级合约时应核对目标宿主支持的能力。

## 命令

| 命令 | 输入与结果 |
| --- | --- |
| `init <Name> --template <类型>` | 在新目录生成项目，拒绝覆盖已有目录 |
| `validate [目录]` | 校验源清单、权限名、网络披露和静态设置，不执行 DLL |
| `build [目录]` | 构建 Release，检查入口与依赖，打印本次独立输出目录 |
| `pack [目录]` | 构建并生成 ZIP 和 `.sha256`，自动包含构建输出依赖，排除宿主 Contracts |
| `doctor <解压目录> [--host-version 0.1.0]` | 显示 SDK 版本，校验包路径、入口类型、公开无参数构造函数与依赖闭包；指定目标版本时检查最低宿主版本 |
| `test [目录]` | 构建插件，按 `plugin.test.json` 在独立 Worker 中检查通知、固定内容交互与停止行为 |

命令失败返回非零退出码。`pack` 不覆盖旧发布包；更新版本后重新打包，或先由开发者自行归档旧包。中间输出位于项目 `.wini`，发布包位于 `dist`。

## 模板

| 类型 | 行为 |
| --- | --- |
| `notification` | 延迟一秒发送一条通知，使用每次运行独立的去重键 |
| `pinned` | 提交计数器，按钮增加、滑块调整步长、开关控制可用状态；停止时撤下并注销回调 |
| `network` | 通过宿主网络服务读取示例状态，`send` 默认关闭；开启前修改实际目的地和用途并核对披露 |

三个模板共用取消、停止和资源释放流程。异常只记录结构化诊断，不输出响应正文或设置值。

## 独立功能测试

`test` 使用工具包附带的 Worker，在独立进程和数据目录运行构建结果。不会调用正式应用或读取已安装插件的设置；网络只提供固定模拟响应，不发送实际请求。输出保存在项目 `.wini/tests/<本次标识>/results.json`，只记录场景序号、通过状态、数量及失败类型，不记录通知正文或设置值。

通知模板的测试计划如下：

```json
{
  "schemaVersion": 1,
  "scenarios": [
    { "minimumNotifications": 1, "maximumNotifications": 1 },
    { "waitMilliseconds": 0, "settleMilliseconds": 1200 }
  ]
}
```

每个场景使用新实例，执行初始化、启动、断言、停止及进程回收。默认运行等待 1500 毫秒、停止后观察 200 毫秒；通知数量默认必须为零。上述第二个场景立即停止，用于检查模板延迟任务的取消。修改插件行为时同步修改预期。

| 可选字段 | 含义 |
| --- | --- |
| `settings` | 本场景的字符串或布尔设置，不写入正式配置 |
| `stubNetwork` / `networkRequests` | 启用固定 HTTP 200 模拟响应，并核对请求次数；默认不提供响应且预期零次 |
| `deniedNetworkRequests` / `diagnostics` | 预期的模拟网络拒绝数和结构化诊断数，默认均为零；网络模板包含一次拒绝场景 |
| `pinnedInteractions` | 按顺序发送现有控件的正常交互，等待每次更新 |
| `expectedCompactText` | 核对交互完成后的固定内容值 |
| `minimumNotifications` / `maximumNotifications` | 本场景允许的通知数量范围 |

测试宿主拒绝非法回调时，场景会失败；显式配置的模拟网络拒绝仍按预期数量核对。测试完成或失败后，工具确认 Worker 退出并清理本次私有容器，结果目录包含 `cleanup.json`。

测试不呈现消息岛窗口，也不替代真实网络、用户授权流程、全局通知去重或宿主界面验收。导入 ZIP 后仍应核对显示、停用与重新开启。独立源代码见[示例库](./samples.md)；模板生成的测试计划可作为自定义场景的起点。
