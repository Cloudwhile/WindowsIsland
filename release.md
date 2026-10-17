# Windows Island v0.1.4

## Fixed

- 修复消息岛安装在 `C:\Program Files` 等受保护目录时，应用内更新无法完成的问题。
- 修复 MSI 升级时无法关闭正在运行的消息岛或释放占用文件而导致安装失败的问题。
- 修复通过 MSI 卸载后安装目录仍有文件残留的问题。

## Improved

- 更新包下载支持断点续传；取消或网络中断后可从已有进度继续。
- 更新页在下载期间显示实时传输速率和完成百分比。

## Upgrade Notes

- 如果 v0.1.3 的 MSI 安装位于 `C:\Program Files` 等受保护目录，应用内更新可能仍会失败；请首次手动运行下方 v0.1.4 MSI，之后的应用内更新即可使用新的权限处理流程。
- 其他安装可直接在消息岛的更新页升级；手动升级时，MSI 用户运行下方安装包，ZIP 用户退出消息岛后将新压缩包完整解压到原目录。
- 接收预发布更新默认关闭；v0.1.4 为正式版，无需开启此选项。

## Downloads

适用于 Windows 10 2004 及以上版本和 Windows 11，x64 设备。

- [MSI 安装包](https://github.com/Cloudwhile/WindowsIsland/releases/download/v0.1.4/WindowsIsland-0.1.4-win-x64.msi)
- [ZIP 压缩包](https://github.com/Cloudwhile/WindowsIsland/releases/download/v0.1.4/WindowsIsland-0.1.4-win-x64.zip)
- 对应的 SHA-256 校验文件见下方附件。

## Full Changelog

https://github.com/Cloudwhile/WindowsIsland/compare/v0.1.3...v0.1.4
