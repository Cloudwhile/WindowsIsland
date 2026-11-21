# Windows Island v0.1.2

> [!IMPORTANT]
>
> 消息岛的维护更新版本，适用于 Windows 10 2004 及以上版本和 Windows 11（x64）设备，推荐已完成系统通知初始化并使用外部插件的用户升级。升级保留已有设置、已安装插件及本地数据。

## Highlights

- 修复已完成系统通知初始化的应用实例启动外部插件（如微信等独立插件）时初始化失败的问题。
- 改善外部插件沙箱异常退出或连接断开时的错误处理，消除误报取消错误和后台未处理异常。

## Fixed

- 修复已初始化应用身份的宿主启动外部插件时，因受隔离插件进程无法读取打包虚拟目录中的运行时与插件文件而导致启动就绪失败的问题。
- 修复外部插件进程提前退出或通信管道断开时误报取消错误并触发后台未观察任务异常的问题。
- 完善插件隔离启动失败时的本机诊断信息，在不记录本地路径和异常正文的前提下保留非零进程退出状态码，便于本机导出排查。

## Upgrade Notes

- 已安装版本可在设置 → **更新** 页直接检查并升级；也可手动下载 [Windows 安装器](https://github.com/Cloudwhile/WindowsIsland/releases/download/v0.1.2/WindowsIsland-0.1.2-win-x64.exe)（[SHA-256](https://github.com/Cloudwhile/WindowsIsland/releases/download/v0.1.2/WindowsIsland-0.1.2-win-x64.exe.sha256)）覆盖安装，安装器会自动关闭同目录运行中的消息岛实例。
- 便携版用户请先退出消息岛，再完整解压 [ZIP 便携版](https://github.com/Cloudwhile/WindowsIsland/releases/download/v0.1.2/WindowsIsland-0.1.2-win-x64.zip)（[SHA-256](https://github.com/Cloudwhile/WindowsIsland/releases/download/v0.1.2/WindowsIsland-0.1.2-win-x64.zip.sha256)）；删除便携目录前，请先在 **初始化** 页注销通知包登记并等待应用退出。
- 插件开发资源：[插件 SDK](https://github.com/Cloudwhile/WindowsIsland/releases/download/v0.1.2/WindowsIsland.Contracts.0.1.2.nupkg)（[SHA-256](https://github.com/Cloudwhile/WindowsIsland/releases/download/v0.1.2/WindowsIsland.Contracts.0.1.2.nupkg.sha256)） · [CLI 工具](https://github.com/Cloudwhile/WindowsIsland/releases/download/v0.1.2/WindowsIsland.Plugin.Cli.0.1.2.nupkg)（[SHA-256](https://github.com/Cloudwhile/WindowsIsland/releases/download/v0.1.2/WindowsIsland.Plugin.Cli.0.1.2.nupkg.sha256)） · [插件示例](https://github.com/Cloudwhile/WindowsIsland/releases/download/v0.1.2/WindowsIsland.Plugin.Samples.0.1.2.zip)（[SHA-256](https://github.com/Cloudwhile/WindowsIsland/releases/download/v0.1.2/WindowsIsland.Plugin.Samples.0.1.2.zip.sha256)）。
- 升级保留既有设置、消息来源偏好、已安装插件与本地数据；**接收预发布更新** 默认关闭，可在更新页自行开启。

## Compatibility

- Windows 10 2004 及以上版本、Windows 11，x64 设备；发布包已包含运行时。
- 微信与 Telegram 等独立官方插件需通过 **插件管理 → 发现插件** 安装并启用，且对应桌面客户端已登录。
- 检查更新和发现插件需要能够连接 GitHub、`raw.githubusercontent.com` 及插件目录服务（[使用文档](https://wini-docs.abszero.cc/) · [插件开发](https://wini-docs.abszero.cc/plugins)）。

## Full Changelog

[查看 v0.1.1 至 v0.1.2 的完整变更](https://github.com/Cloudwhile/WindowsIsland/compare/v0.1.1...v0.1.2)。
