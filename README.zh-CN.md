<div align="center">

<h1>Windows Island</h1>

<p><a href="README.md">English</a> · 简体中文</p>

<p>Windows 消息岛，让通知在你喜欢的位置轻巧呈现。</p>

<p>
  <a href="https://github.com/Cloudwhile/WindowsIsland/releases"><img src="https://img.shields.io/github/v/release/Cloudwhile/WindowsIsland?include_prereleases&amp;sort=semver&amp;style=for-the-badge&amp;label=Release&amp;color=168B8F&amp;labelColor=1F2937" alt="最新发布版本（含预发布）"></a>
  <a href="https://github.com/Cloudwhile/WindowsIsland/actions/workflows/ci.yml"><img src="https://img.shields.io/github/actions/workflow/status/Cloudwhile/WindowsIsland/ci.yml?branch=master&amp;style=for-the-badge&amp;label=Build&amp;labelColor=1F2937" alt="Windows 构建状态"></a>
  <img src="https://img.shields.io/badge/Windows-Desktop-0078D4?style=for-the-badge&amp;labelColor=1F2937" alt="Windows 桌面应用">
  <img src="https://img.shields.io/badge/WinUI-3-0F9D92?style=for-the-badge&amp;labelColor=1F2937" alt="WinUI 3">
  <img src="https://img.shields.io/badge/.NET-10-7957D5?style=for-the-badge&amp;logo=dotnet&amp;logoColor=white&amp;labelColor=1F2937" alt=".NET 10">
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-Apache_2.0-D99A24?style=for-the-badge&amp;labelColor=1F2937" alt="Apache License 2.0"></a>
</p>

<p>
  <a href="#开始使用">开始使用</a> ·
  <a href="#消息来源">消息来源</a> ·
  <a href="#设置">设置</a> ·
  <a href="https://wini-docs.abszero.cc/">使用文档</a>
</p>

</div>

消息岛将桌面通知汇集到轻巧的浮窗中，显示应用图标、会话头像和消息内容。八个屏幕位置自由选择，通知保持置顶且不抢占输入焦点，显示后自动收起。

## 开始使用

在 [Releases](https://github.com/Cloudwhile/WindowsIsland/releases) 下载消息岛。支持 Windows 10 2004 及以上版本和 Windows 11，适用于 x64 电脑。

| 下载包 | 打开方式 |
| --- | --- |
| Windows 安装器（.exe） | 允许管理员授权，选择安装目录后从开始菜单打开消息岛 |
| ZIP 便携版 | 解压后运行 WindowsIsland.exe |

1. 在 **初始化** 页点击 **应用初始化** 右侧图标。
2. 点击 **通知访问** 右侧图标，在 Windows 弹窗中允许访问通知。
3. 在 **消息来源** 页选择需要的来源，通过预览查看效果，再到 **外观** 页选择位置和动画。
4. 点击右上角的完成图标，应用继续在系统托盘中运行。

便携版删除前，请在 **初始化** 页选择 **注销稀疏包注册**，确认后等待消息岛退出，再删除文件夹。首次使用系统通知可能需要一次管理员授权；便携版不会创建开始菜单、自启或卸载入口。

设置自动保存。点击托盘图标重新打开设置；右键托盘图标可查看状态或退出。通知访问需要授权时，可通过初始化页的 Windows 设置入口授权，然后刷新或重试。

## 通知体验

| 功能 | 表现 |
| --- | --- |
| 图标与头像 | 显示可用的会话头像，缺少头像时使用应用图标 |
| 原生外观 | 跟随 Windows 浅色、深色主题 |
| 保持焦点 | 通知置顶显示，不抢占当前应用的输入焦点 |
| 点击操作 | 左键打开对应程序，右键关闭弹窗 |
| 八个位置 | 可选择屏幕四角或四边中部 |
| 平滑动画 | 从所选边缘展开并收起，也可关闭动画 |
| 长消息 | 正文自动换行并省略超出部分，保持图标完整 |
| 自动收起 | 每条通知显示约五秒，新消息到达时刷新弹窗 |
| 消息合并 | 同一消息来自多个来源时合并展示 |

## 消息来源

消息来源以内置插件提供，各自拥有独立开关和连接状态。

| 来源 | 内容 |
| --- | --- |
| 系统通知 | Windows 新通知，包括 QQ 等应用的通知 |
| 微信 | 已登录桌面客户端的新消息和未读预览，显示可用的会话头像，尊重会话免打扰设置 |
| Telegram | 已登录桌面客户端的新消息 |
| 电源 | 接入电源、开始充电、断开电源和充满电时的提示 |

来源连接或重新开启后，从新消息开始提醒。微信免打扰会话保持安静，历史消息不会重放；关闭一个来源不影响其他来源。

接收系统通知需要完成初始化并允许通知访问。在此之前，已连接的微信等独立来源和通知预览仍可使用。

## 设置

侧边栏包含 **初始化**、**消息来源**、**插件列表**、**外观**、**更新** 和 **诊断与反馈**。

| 设置 | 可选行为 |
| --- | --- |
| 开机自启 | 在初始化页开启并批准 Windows 管理员权限请求，登录后在后台运行 |
| 游戏与全屏静音 | 在消息来源页分别控制两个开关，默认均开启 |
| 系统免打扰 | 在消息来源页切换，减少 Windows 横幅打扰，消息岛继续接收消息；系统优先通知例外仍然生效 |
| 位置与动画 | 在外观页选择屏幕位置、开关动画并预览通知 |
| 语言 | 在外观页选择简体中文、English 或跟随系统，默认使用简体中文 |

检测到已启用的游戏或全屏静音条件时，消息岛暂停弹窗；结束后恢复新消息提醒，期间的消息不会补弹。系统免打扰为独立设置。

## 应用更新

在 **更新** 页点击检查图标，查看可用版本与发布说明。点击下载图标准备更新，应用重启后完成安装并保留已有偏好；下载取消或网络中断后可从已有进度继续，更新页会显示当前百分比和传输速率。

**接收预发布更新** 默认关闭，需要体验预发布版本时可自行开启。

## 诊断与反馈

在 **诊断与反馈** 页点击生成图标，手动生成本机诊断报告。可预览、复制或导出报告，也可控制本机诊断采集和清除已存记录。

报告不会自动上传，不包含消息正文、头像或账号信息。反馈图标打开 [GitHub Issue 表单](https://github.com/Cloudwhile/WindowsIsland/issues/new/choose)，由你决定是否附加报告。Issue 为公开内容，提交前请检查文字与附件。

## 从源码构建

在仓库根目录执行：

```powershell
dotnet build src/WindowsIsland/WindowsIsland.csproj -c Release -p:Platform=x64
dotnet publish src/WindowsIsland/WindowsIsland.csproj -c Release -p:Platform=x64
```

发布文件生成在 `artifacts/publish/`。复制整个目录后，打开 `WindowsIsland.exe`。

## 许可证

本项目使用 [Apache License 2.0](LICENSE)。
