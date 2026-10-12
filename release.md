# Windows Island v0.1.1

> [!IMPORTANT]
>
> 本次更新修复完成初始化后无法启动更新程序的问题。如果旧版本提示“更新程序未能准备就绪”，请使用下方 MSI 或 ZIP 手动升级到 v0.1.1。

## Fixed

- 修复已初始化应用下载更新后，更新程序找不到文件而无法启动的问题。
- 改善更新失败时的本机错误记录，保留启动与预检失败的原因。

## Upgrade Notes

- MSI 用户下载并运行安装包升级；ZIP 用户退出消息岛后，将新压缩包完整解压到原目录。
- 升级后可继续通过设置中的“更新”页下载并安装后续版本。
- 接收预发布更新默认关闭；v0.1.1 为正式版，无需开启此选项。

## Downloads

适用于 Windows 10 2004 及以上版本和 Windows 11，x64 设备。

- [MSI 安装包](https://github.com/Cloudwhile/WindowsIsland/releases/download/v0.1.1/WindowsIsland-0.1.1-win-x64.msi)
- [ZIP 压缩包](https://github.com/Cloudwhile/WindowsIsland/releases/download/v0.1.1/WindowsIsland-0.1.1-win-x64.zip)
- 对应的 SHA-256 校验文件见下方附件。

## Full Changelog

https://github.com/Cloudwhile/WindowsIsland/compare/v0.1.0...v0.1.1
