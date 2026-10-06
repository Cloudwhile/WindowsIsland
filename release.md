# Windows Island v0.1.0-rc.4

> [!IMPORTANT]
>
> 消息岛的第四个候选版本，重点修复更新时解压后再次下载和更新准备失败的问题。此版本为预发布版本，微信接入仍处于实验阶段。遇到旧版更新失败时，请使用下方手动升级入口。

## Highlights

- MSI 安装版直接下载对应的 MSI，ZIP 版本只下载并解压 ZIP，避免更新过程中重复准备安装包。
- 改善更新程序启动较慢时的处理，并提供明确的超时提示。

## Fixed

- 修复 MSI 安装版更新时先下载并解压 ZIP、随后再次下载 MSI 的问题。
- 修正 MSI 更新仍依赖 ZIP 解压文件和文件清单的准备流程，避免不必要的检查阻止更新继续。
- 延长更新程序的启动等待，区分启动超时与用户取消；准备失败时保留原版本和失败诊断。

## Upgrade Notes

- 从 `v0.1.0-rc.3` 升级时，如果自动更新失败并停留在设置页，请手动下载 [MSI 安装包](https://github.com/Cloudwhile/WindowsIsland/releases/download/v0.1.0-rc.4/WindowsIsland-0.1.0-rc.4-win-x64.msi) 安装；ZIP 版本请先退出消息岛，再完整解压 [ZIP 压缩包](https://github.com/Cloudwhile/WindowsIsland/releases/download/v0.1.0-rc.4/WindowsIsland-0.1.0-rc.4-win-x64.zip) 使用。
- 从 `v0.1.0-rc.1` 或 `v0.1.0-rc.2` 升级时，也可使用上述手动入口。旧版更新检查受到 GitHub 请求额度限制时，需要先手动安装新版。
- “接收预发布更新”默认关闭，希望继续接收候选版本时请在设置的“更新”页开启。
- 已有消息来源、语言、方位、动画、静音和自启偏好会保留。

## Known Issues

- 微信接入仍为实验性功能，不同客户端版本和窗口状态下，消息与头像读取可能存在差异；仅提醒未开启消息免打扰的会话。
- 游戏识别以系统报告为准，窗口化和部分无边框游戏可能无法识别。

## Compatibility

- Windows 10 2004 及以上版本、Windows 11，x64 设备。
- Telegram 和微信消息接入需要对应桌面客户端已登录。
- 检查和下载更新需要能够连接 GitHub 及 `raw.githubusercontent.com`。

## Full Changelog

[查看 v0.1.0-rc.3 至 v0.1.0-rc.4 的完整变更](https://github.com/Cloudwhile/WindowsIsland/compare/v0.1.0-rc.3...v0.1.0-rc.4)。
