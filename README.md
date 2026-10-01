# Windows Island

使用 C# 和 WinUI 3 实现的 Windows 通知灵动岛。启动后常驻系统托盘，收到通知时才在屏幕工作区顶部居中显示。

## 使用

- 待机时岛完全隐藏，只保留托盘图标并后台监听。
- 收到新系统通知时岛自动出现，仅显示应用名、标题和正文，5 秒后收起并完全隐藏。连续通知显示最新一条并重新计时。
- 点击正在显示的通知不会切换页面或打断自动隐藏，也不会弹出额外面板。
- 岛上没有音乐、计时、设置、收起或关闭控件。启动不重播通知中心的历史消息。
- 右键托盘图标可查看监听状态、处理通知授权或退出应用；打开托盘菜单不会显示岛。
- 消息回复尚未接入。

## 开发与构建

需要 Windows 10 2004 或更新版本、.NET 10 SDK，构建目标为 x64。推荐在 Windows 11 上运行。

直接在支持 .NET 10 的 Visual Studio 中打开 `src/WindowsIsland/WindowsIsland.csproj`，选择 x64 和 `Windows Island` 启动配置。以下终端命令在仓库根目录运行：

```powershell
dotnet build src/WindowsIsland/WindowsIsland.csproj -c Release -p:Platform=x64
& .\artifacts\bin\WindowsIsland\x64\Release\net10.0-windows10.0.19041.0\win-x64\WindowsIsland.exe
```

生成可复制的独立运行目录：

```powershell
dotnet publish src/WindowsIsland/WindowsIsland.csproj -c Release -p:Platform=x64
```

复制整个 `artifacts/publish` 目录运行，不能只复制 EXE。应用采用非 MSIX、自包含部署，随产物携带 .NET 和 Windows App SDK 运行时。

## 启用系统通知

通知监听使用 Windows `UserNotificationListener`，需要应用包身份、`userNotificationListener` 能力和用户授权。仅直接运行未注册的 EXE 无法监听其他应用的通知。

在已启用 Windows 开发者模式的电脑上，关闭正在运行的 Windows Island，在仓库根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/register-notifications.ps1
```

脚本发布到 `artifacts/publish/`，生成图标和注册清单，并仅为当前用户注册应用。随后从开始菜单启动 **Windows Island**，在 Windows 弹窗中允许访问通知。拒绝后可通过托盘菜单进入系统通知访问设置；关闭授权弹窗后也可从托盘菜单再次请求。脚本不会自动修改开发者模式或代替用户授权。

已发布时可加 `-NoBuild`；只准备文件、不注册时加 `-PrepareOnly`。本地开发注册会自动递增版本以更新清单。注册后请保留发布目录；需要移动时应重新注册。该流程用于本机开发，正式分发仍需签名的 MSIX 安装流程。生成的 `artifacts/packages/` 测试包未签名。

应用运行期间通过通知变更事件触发读取，并每秒同步一次作为补充。系统通知中心中的原通知保持不变；退出应用后停止监听。撤销权限会清除岛上当前通知并停止读取，重新授权时不重播历史消息。

## 验证

```powershell
dotnet run --project tests/WindowsIsland.LogicTests/WindowsIsland.LogicTests.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -Command "& ([scriptblock]::Create((Get-Content -Raw -Encoding UTF8 scripts/verify-notifications.ps1)))"
```

第二条命令需要注册版应用运行、已授权且处于待机状态，会发送本应用的本地测试通知，验证托盘常驻、待机隐藏、点击不展开面板、没有操作控件、通知自动出现后完全隐藏，以及连续通知刷新计时，最后只移除本次测试通知。测试截图写入 `artifacts/verification/`。

## 结构

```text
src/WindowsIsland/       应用项目与源码
  components/           通知内容与主题
  Services/             通知监听、展示状态、系统托盘与原生窗口接口
  Assets/               托盘图标源资源
  Properties/           Visual Studio 启动配置
scripts/                验证脚本
packaging/              通知能力与应用身份清单
tests/                  通知去重和展示状态逻辑测试
artifacts/              生成文件（不提交 Git）
  bin/WindowsIsland/    编译输出
  obj/WindowsIsland/    中间文件与依赖还原缓存
  publish/              可分发应用
  verification/         界面验证截图
Directory.Build.props   统一输出路径配置
```

构建与发布路径由根目录的 `Directory.Build.props` 统一配置，源码目录中不生成 `bin`、`obj`。

当前版本不设置开机启动，也不保存通知历史。

窗口实现参考微软的 [窗口管理文档](https://learn.microsoft.com/zh-cn/windows/apps/develop/ui/manage-app-windows)，部署参考 [非打包 WinUI 3 应用文档](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app)。
通知实现参考微软的 [通知监听文档](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/notification-listener)。
