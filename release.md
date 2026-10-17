# Windows Island v0.1.5

## Fixed

- 修复初始化资源写入受保护安装目录时无响应或重复失败的问题，并保留失败原因以便重试。
- 修复 MSI 首次安装、升级、修复和卸载时无法关闭目标安装目录中仍在运行的消息岛的问题；无响应进程只在超时并完成身份核对后处理，不影响其他目录实例。
- 修复弹窗收起期间更新消息造成透明度叠加、收起不平滑的问题。
- 修复系统高对比度或透明效果不可用时外观设置可能阻止应用启动的问题。

## Improved

- 在“设置 → 外观”提供毛玻璃开关、背景透明度滑块和透明度过渡开关。
- 背景透明度只影响弹窗背景，正文、应用图标和会话头像保持清晰；偏好逐项校验并在重启后保留。
- 透明度过渡使用线性动画，关闭过渡不会关闭弹窗形状动画。
- 保持 MSI 安装路径可选；只有选定目录需要时才申请管理员权限。

## Upgrade Notes

- v0.1.5 为正式版，默认不接收预发布更新。
- MSI 用户运行 MSI 安装包，ZIP 用户退出消息岛后将压缩包完整解压到原目录。

## Downloads

适用于 Windows 10 2004 及以上版本和 Windows 11，x64 设备。

- [MSI 安装包](https://github.com/Cloudwhile/WindowsIsland/releases/download/v0.1.5/WindowsIsland-0.1.5-win-x64.msi)
- [ZIP 压缩包](https://github.com/Cloudwhile/WindowsIsland/releases/download/v0.1.5/WindowsIsland-0.1.5-win-x64.zip)
- 对应的 SHA-256 校验文件见下方附件。

## Full Changelog

https://github.com/Cloudwhile/WindowsIsland/compare/v0.1.4...v0.1.5
