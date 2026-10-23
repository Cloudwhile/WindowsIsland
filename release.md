# Windows Island v0.1.0

消息岛将 Windows 桌面通知汇集到屏幕边缘，在你选择的位置显示应用图标、会话头像和消息内容，保持当前应用的输入焦点。

## 功能

- 内置系统通知、微信、Telegram 和电源来源，各自拥有独立开关、连接状态及重试入口。
- 普通消息显示约五秒，新消息重新计时；跨来源的相同消息合并展示，重新连接不重放历史消息。
- 支持插件固定内容、展开详情及按钮、开关、滑块交互；普通消息优先显示。
- 提供八个位置、动画、毛玻璃、背景透明度和透明度过渡，适配 Windows 明暗主题。
- 游戏与全屏静音可独立设置，系统免打扰单独控制。
- 支持手动导入插件 ZIP、插件设置、启停、更新与删除；提供独立插件合约和开发文档。
- 支持简体中文、English 和跟随系统，设置自动保存。
- 应用内更新提供版本说明、下载进度、传输速率及中断续传，更新后保留偏好。
- 本机诊断支持预览、复制、导出和清除，不自动上传报告。

## 开始使用

1. 下载安装器，选择目录并按提示授权；也可完整解压便携包后运行 `WindowsIsland.exe`。
2. 按需在 **初始化** 页完成系统通知初始化与通知访问授权。已连接的微信等独立来源和通知预览可直接使用。
3. 在 **消息来源** 页开启需要的来源，在 **外观** 页调整位置与显示效果。
4. 关闭设置后，消息岛继续在系统托盘运行；安装版可在初始化页开启开机自启。

安装器在覆盖安装时关闭同目录的消息岛实例。卸载保留用户数据；便携版删除前，请在初始化页注销通知包登记并等待应用退出。

**接收预发布更新** 默认关闭，可在更新页自行开启。

## 下载

适用于 Windows 10 2004 及以上版本和 Windows 11，x64 设备。发布包已包含运行时。

- [Windows 安装器](https://github.com/Cloudwhile/WindowsIsland/releases/download/v0.1.0/WindowsIsland-0.1.0-win-x64.exe) · [SHA-256](https://github.com/Cloudwhile/WindowsIsland/releases/download/v0.1.0/WindowsIsland-0.1.0-win-x64.exe.sha256)
- [ZIP 便携版](https://github.com/Cloudwhile/WindowsIsland/releases/download/v0.1.0/WindowsIsland-0.1.0-win-x64.zip) · [SHA-256](https://github.com/Cloudwhile/WindowsIsland/releases/download/v0.1.0/WindowsIsland-0.1.0-win-x64.zip.sha256)
- [使用文档](https://wini-docs.abszero.cc/) · [插件开发](https://wini-docs.abszero.cc/plugins)
