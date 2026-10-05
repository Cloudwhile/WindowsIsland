<div align="center">

<h1>Windows Island</h1>

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
  <a href="#从源码构建">从源码构建</a>
</p>

</div>

新通知默认在屏幕顶部居中出现，也可选择屏幕四角或四边中部，显示应用图标、会话头像和消息内容，随后自动收起。待机时隐藏窗口，保留系统托盘入口。

> [!NOTE]
> **微信接入仍为实验性功能。** 已加入已登录客户端的会话监听，当前可读取会话列表；真实新消息接管和更多客户端版本仍待验证。

## 开始使用

在 [Releases](https://github.com/Cloudwhile/WindowsIsland/releases) 下载 MSI 安装包，安装后从开始菜单打开 Windows Island；也可下载 ZIP 压缩包，完整解压后运行。

1. 打开应用目录中的 `WindowsIsland.exe`，首次启动进入设置页。
2. 在 **初始化** 页点击 **应用初始化** 右侧图标，完成应用初始化。
3. 点击 **通知访问** 右侧图标，在 Windows 弹窗中允许访问通知。
4. 通过 **通知预览** 查看显示效果，在 **消息来源** 页选择需要的来源，再点击右上角的完成图标。

以后直接运行 `WindowsIsland.exe` 即可。设置会自动保存，重复启动会打开已有实例的设置页。

通知访问未获允许时，可在设置页打开系统设置后重新授权，也可通过刷新状态和重试入口继续初始化。

希望仅显示消息岛时，展开设置页的 **仅使用消息岛通知**，按引导关闭对应应用的 Windows 通知横幅，保留通知中心和消息岛的系统通知来源。

## 通知体验

| 体验 | 表现 |
| --- | --- |
| 图标与头像 | 原通知提供会话头像时，左侧显示圆形头像，应用图标显示在应用名旁；其他通知显示应用图标 |
| 原生外观 | WinUI 3 弹窗跟随系统浅色、深色主题，应用名、发送者、正文和时间分别排列 |
| 置顶显示 | 通知显示期间保持置顶，出现、更新和调整大小时保持当前应用的输入焦点 |
| 点击操作 | 左键打开对应程序，右键关闭弹窗；没有可打开的程序时继续显示到自动收起 |
| 弹窗位置 | 左上、左中、左下、中下、右下、右中、右上、中上八个位置，选择后自动保存 |
| 平滑动画 | 从所选位置的边缘细线展开为信息药丸，退出时沿原方向收起；左右中部使用竖线，可在设置中关闭动画 |
| 内容适配 | 窗口随消息内容调整大小，长消息自动换行并省略超出部分 |
| 自动收起 | 每条通知显示约 5 秒，连续收到消息时显示最新一条并重新计时 |
| 消息合并 | 同一消息来自多个来源时合并展示，并保留可用的应用图标与会话头像 |

启动和重新授权时从新消息开始监听，Windows 通知中心中的原通知保持不变。

## 消息来源

在设置页中分别开启或关闭各个来源：

| 来源 | 状态 | 内容 |
| --- | --- | --- |
| 系统通知 | 已接入 | Windows 通知，包括 QQ 等应用的新通知 |
| Telegram | 已接入 | 已登录 Telegram 客户端的新消息 |
| 电源 | 已接入 | 接入电源、开始充电、断开电源和充满电时的状态提示 |
| 微信 | 实验性接入 | 监听已登录客户端的会话和未读预览，仅提醒未开启消息免打扰的会话；实际消息接管仍待验证 |

关闭系统通知来源后，其他已开启的来源继续监听。微信首次连接、重新启用和客户端重启时会从当前会话建立起点，避免重放历史消息。

## 托盘与设置

设置通过侧边栏切换 **初始化**、**消息来源**、**外观** 和 **更新**。窄窗口使用图标导航，较长内容可滚动。

在 **外观 → 语言** 中选择简体中文、English 或跟随系统，界面会立即切换并保存选择。默认使用简体中文；未支持的系统语言也会回退到简体中文。

- **打开设置**：点击托盘图标。
- **查看状态**：右键托盘图标，可查看监听状态并打开设置。
- **隐藏通知**：右键点击通知窗口，应用继续在后台运行。
- **退出应用**：在托盘菜单中选择“退出”。

## 应用更新

在设置的 **更新** 页点击检查图标，查看 GitHub Release 的可用版本与发布说明。发现新版本后，点击下载图标准备更新，完成后消息岛会重启。下载支持取消，失败后可重试。

**接收预发布更新** 默认关闭；开启后会同时检查候选版本。更新会校验下载文件，保留已有偏好；MSI 安装的应用使用安装包升级，ZIP 版本在替换失败时恢复原文件。

`v0.1.0-rc.1` 尚无应用内更新入口，需要先从 [Releases](https://github.com/Cloudwhile/WindowsIsland/releases) 下载新版安装包或 ZIP 压缩包。

## 从源码构建

在仓库根目录执行：

```powershell
dotnet build src/WindowsIsland/WindowsIsland.csproj -c Release -p:Platform=x64
dotnet publish src/WindowsIsland/WindowsIsland.csproj -c Release -p:Platform=x64
```

发布文件生成在 `artifacts/publish/`。复制整个发布目录后，打开其中的 `WindowsIsland.exe`，按设置页引导完成初始化。

分支推送和 PR 会自动构建、运行测试，并在 [GitHub Actions](https://github.com/Cloudwhile/WindowsIsland/actions/workflows/ci.yml) 中提供 Windows x64 的 ZIP 压缩包和 MSI 安装包。推送 `v1.2.3` 形式的版本标签后，同一流程通过才会上传到 [Releases](https://github.com/Cloudwhile/WindowsIsland/releases)，同时提供各自的 SHA-256 校验文件；`v1.2.3-rc.1` 等标签会标记为预发布版本。

<details>
<summary>验证与桌面检查</summary>

在仓库根目录执行：

```powershell
dotnet run --project tests/WindowsIsland.LogicTests/WindowsIsland.LogicTests.csproj -c Release
dotnet run --project tests/WindowsIsland.TelegramHookTests/WindowsIsland.TelegramHookTests.csproj -c Release
dotnet run --project tests/WindowsIsland.WeChatTests/WindowsIsland.WeChatTests.csproj -c Release
```

验证覆盖通知去重、显示计时、消息合并、设置保存、图标与头像读取，以及 Telegram 和微信测试客户端的消息接收、最小化监听和连接恢复。

验证生成的文件保存在 `artifacts/verification/`。

</details>

## 许可证

本项目使用 [Apache License 2.0](LICENSE)。
