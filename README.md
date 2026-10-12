<div align="center">

<h1>Windows Island</h1>

<p>English · <a href="README.zh-CN.md">简体中文</a></p>

<p>A compact notification island for your Windows desktop.</p>

<p>
  <a href="https://github.com/Cloudwhile/WindowsIsland/releases"><img src="https://img.shields.io/github/v/release/Cloudwhile/WindowsIsland?include_prereleases&amp;sort=semver&amp;style=for-the-badge&amp;label=Release&amp;color=168B8F&amp;labelColor=1F2937" alt="Latest release, including prereleases"></a>
  <a href="https://github.com/Cloudwhile/WindowsIsland/actions/workflows/ci.yml"><img src="https://img.shields.io/github/actions/workflow/status/Cloudwhile/WindowsIsland/ci.yml?branch=master&amp;style=for-the-badge&amp;label=Build&amp;labelColor=1F2937" alt="Windows build status"></a>
  <img src="https://img.shields.io/badge/Windows-Desktop-0078D4?style=for-the-badge&amp;labelColor=1F2937" alt="Windows desktop app">
  <img src="https://img.shields.io/badge/WinUI-3-0F9D92?style=for-the-badge&amp;labelColor=1F2937" alt="WinUI 3">
  <img src="https://img.shields.io/badge/.NET-10-7957D5?style=for-the-badge&amp;logo=dotnet&amp;logoColor=white&amp;labelColor=1F2937" alt=".NET 10">
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-Apache_2.0-D99A24?style=for-the-badge&amp;labelColor=1F2937" alt="Apache License 2.0"></a>
</p>

<p>
  <a href="#getting-started">Getting started</a> ·
  <a href="#notification-sources">Notification sources</a> ·
  <a href="#building-from-source">Building from source</a>
</p>

</div>

New notifications appear in a compact island at the top of your screen, or at any of eight positions you choose. The island shows the app icon, conversation avatar and message, then folds away. A system tray icon keeps settings within reach while the island is idle.

## Getting started

Download the MSI installer from [Releases](https://github.com/Cloudwhile/WindowsIsland/releases), install it and open Windows Island from the Start menu. Alternatively, extract the entire ZIP archive and run `WindowsIsland.exe`.

1. Open **Setup** and click the icon beside **App initialization**.
2. Click the icon beside **Notification access** and allow access in the Windows prompt.
3. Use the notification preview to check the appearance, then choose your notification sources.
4. Click the completion icon in the upper right.

Settings are saved automatically. Launching the app again opens the existing instance's settings. If notification access is unavailable, use the Windows settings shortcut to manage permission, then refresh or retry.

To reduce Windows banner interruptions, turn on **Sources → Windows do not disturb**. Windows Island continues receiving messages, while Windows retains your priority notification exceptions.

## Notification experience

| Feature | Behavior |
| --- | --- |
| Icons and avatars | Shows a conversation avatar when available, with the app icon beside its name; otherwise uses the app icon |
| Native appearance | Follows the Windows light or dark theme, with separate app, sender, message and time fields |
| Always on top | Displays above other windows without taking keyboard focus |
| Mouse actions | Left-click opens the source app when available; right-click dismisses the notification |
| Eight positions | Top left, middle left, bottom left, bottom center, bottom right, middle right, top right and top center |
| Smooth animation | Expands from a thin line at the chosen screen edge and folds back when dismissed; animations can be disabled |
| Long messages | Wraps text and truncates overflow while keeping icons visible |
| Automatic dismissal | Shows each notification for about five seconds; a new message updates the island and restarts the timer |
| Message merging | Combines copies of the same message from multiple sources and keeps available icons and avatars |

Startup and renewed authorization begin with new messages. Existing notifications remain in Windows Notification Center.

## Notification sources

Sources are provided as built-in plugins. Enable or disable them independently and check their connection status in settings.

| Source | Notifications |
| --- | --- |
| Windows notifications | New Windows notifications, including those from apps such as QQ |
| Telegram | New messages from a signed-in desktop client |
| WeChat | New messages and unread previews from a signed-in desktop client, respecting muted conversations and adding avatars when available |
| Power | Power connection, charging, disconnection and full-charge events |

Disabling Windows notifications leaves other enabled sources running. WeChat starts from the current conversation state on first connection, re-enabling or client restart, so previous messages are not replayed. Available message and avatar information depends on the desktop client; an app icon is used when an avatar is unavailable.

**Mute during games** and **Mute during full screen** are independent switches, both enabled by default. When either enabled condition applies, the island dismisses its current notification and pauses popups. It resumes with new messages afterward, without replaying the backlog. Game detection uses the full-screen game state reported by Windows; windowed games and some borderless games fall outside this detection scope.

## Tray and settings

The sidebar contains **Setup**, **Sources**, **Appearance**, **Updates**, and **Diagnostics & feedback**. Narrow windows use icon navigation, and longer pages scroll.

- **Startup:** turn on **Launch at startup** in Setup and approve the Windows administrator prompt. Canceling the prompt keeps the previous setting. Login launches the app in the background.
- **Do not disturb:** toggle Windows do not disturb from Sources, or use the adjacent icon to open Windows notification settings. This is independent of the island's game and full-screen mute settings.
- **Language:** select Simplified Chinese, English or the system language in Appearance. Changes apply immediately and are saved. The app defaults to Simplified Chinese, which is also the fallback for unsupported system languages.
- **Open settings:** click the tray icon.
- **Check status or exit:** right-click the tray icon.
- **Dismiss a notification:** right-click the island; message listening continues.

## Updates

Open **Updates** and click the check icon to see available releases and their notes. When an update is available, click download to prepare it; Windows Island restarts to finish updating. Downloads can be canceled or retried.

**Include prerelease updates** is off by default. Enable it to receive release candidates. Updates verify downloaded files and preserve your preferences. MSI installations upgrade through the installer; ZIP installations restore the previous files if replacement fails.

## Diagnostics and feedback

Use **Diagnostics & feedback** to manually generate, preview, copy, export and clear local diagnostic reports. Reports are never uploaded automatically. The feedback button opens a GitHub Issue form, where you choose whether to attach a report.

Reports exclude message text, avatars and account information. GitHub Issues are public, so review any text and attachments before submitting them.

## Building from source

Run from the repository root:

```powershell
dotnet build src/WindowsIsland/WindowsIsland.csproj -c Release -p:Platform=x64
dotnet publish src/WindowsIsland/WindowsIsland.csproj -c Release -p:Platform=x64
```

Published files are placed in `artifacts/publish/`. Copy the entire directory, run `WindowsIsland.exe` and follow the setup steps.

## License

[Apache License 2.0](LICENSE).
