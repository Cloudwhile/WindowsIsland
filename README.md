# Windows Island

Windows 通知灵动岛。新通知在屏幕顶部居中出现，显示应用图标、会话头像和消息内容，随后自动收起。待机时隐藏窗口，保留系统托盘入口。

## 开始使用

1. 打开应用目录中的 `WindowsIsland.exe`，首次启动进入设置页。
2. 点击“应用初始化”右侧图标，完成应用初始化。
3. 点击“通知访问”右侧图标，在 Windows 弹窗中允许访问通知。
4. 通过“通知预览”查看显示效果，选择需要的消息来源，再点击右上角的完成图标。

以后直接运行 `WindowsIsland.exe` 即可。设置会自动保存，重复启动会打开已有实例的设置页。

通知访问未获允许时，可在设置页打开系统设置后重新授权。设置页也提供刷新状态和重试入口。

## 通知体验

- 通知窗口随消息内容调整大小，长消息自动换行并省略超出部分。
- 原通知提供会话头像时，左侧显示圆形头像，应用图标显示在应用名旁；其他通知显示应用图标。
- 弹窗跟随系统浅色、深色主题，应用名、发送者、正文和时间分别排列。
- 通知显示期间保持置顶，出现、更新和调整大小时保持当前应用的输入焦点。
- 每条通知显示约 5 秒，连续收到消息时显示最新一条并重新计时。
- 同一消息来自多个来源时合并展示，并保留可用的应用图标与会话头像。
- 启动和重新授权时从新消息开始监听，Windows 通知中心中的原通知保持不变。

## 消息来源

在设置页中分别开启或关闭以下来源：

| 来源 | 内容 |
| --- | --- |
| 系统通知 | Windows 通知，包括 QQ 等应用的新通知 |
| 微信 | 已登录微信客户端的新消息 |
| Telegram | 已登录 Telegram 客户端的新消息 |
| 电源 | 接入电源、开始充电、断开电源和充满电时的状态提示 |

关闭系统通知后，仍可使用已开启的微信、Telegram 和电源来源。

## 托盘与设置

- 点击托盘图标打开设置页。
- 右键托盘图标可查看监听状态、打开设置或退出应用。
- 关闭通知窗口会隐藏当前消息，应用继续在后台运行。
- 通过托盘菜单中的“退出”结束应用。

## 从源码构建

在仓库根目录执行：

```powershell
dotnet build src/WindowsIsland/WindowsIsland.csproj -c Release -p:Platform=x64
dotnet publish src/WindowsIsland/WindowsIsland.csproj -c Release -p:Platform=x64
```

发布文件生成在 `artifacts/publish/`。复制整个发布目录后，打开其中的 `WindowsIsland.exe`，按设置页引导完成初始化。

## 验证

```powershell
dotnet run --project tests/WindowsIsland.LogicTests/WindowsIsland.LogicTests.csproj -c Release
dotnet run --project tests/WindowsIsland.TelegramHookTests/WindowsIsland.TelegramHookTests.csproj -c Release
```

验证覆盖通知去重、显示计时、消息合并、设置保存、图标与头像读取，以及 Telegram 消息接收和连接恢复。

`scripts/` 中还提供系统通知、客户端弹窗和 Telegram 的桌面验证工具，验证结果与截图保存到 `artifacts/verification/`。
