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
  <a href="#settings">Settings</a> ·
  <a href="https://wini-docs.abszero.cc/">Documentation (简体中文)</a>
</p>

</div>

Windows Island brings desktop notifications into a compact island with app icons, conversation avatars and messages. Choose one of eight screen positions, keep your keyboard focus, and let the island fold away after each notification.

## Getting started

Download Windows Island from [Releases](https://github.com/Cloudwhile/WindowsIsland/releases). Supports Windows 10 version 2004 or later and Windows 11, on x64 PCs.

| Package | How to open |
| --- | --- |
| Windows installer (.exe) | Allow administrator access, choose a folder, then open Windows Island from the Start menu |
| Portable ZIP | Extract and run WindowsIsland.exe |

1. In **Setup**, click the icon beside **App initialization**.
2. Click the icon beside **Notification access** and allow access in the Windows prompt.
3. Choose your sources in **Sources**. Use the preview to check the island, then select its position and animation in **Appearance**.
4. Click the completion icon in the upper right. The app remains available in the system tray.

Before deleting a portable copy, open **Setup**, select **Unregister notification package**, confirm, and wait for Windows Island to exit. First use of system notifications may require administrator access once. The portable package creates no Start menu, startup, or uninstall entry.

Settings save automatically. Click the tray icon to reopen settings, or right-click it to check status and exit. If notification access needs attention, use the Windows settings shortcut in Setup, then refresh or retry.

## Notification experience

| Feature | Behavior |
| --- | --- |
| Icons and avatars | Displays conversation avatars when available, with an app icon as the fallback |
| Native appearance | Follows the Windows light or dark theme |
| Focus stays yours | Notifications remain on top without taking keyboard focus |
| Mouse actions | Left-click opens the source app when available; right-click dismisses the island |
| Eight positions | Choose any corner or the center of any screen edge |
| Animation | Expands from the chosen edge and folds away; animations can be turned off |
| Long messages | Wraps text and truncates overflow while keeping icons visible |
| Automatic dismissal | Each notification stays for about five seconds; new messages refresh the island |
| Message merging | Combines copies of the same message from multiple sources |

## Notification sources

Sources are built-in plugins with independent switches and connection status.

| Source | Notifications |
| --- | --- |
| Windows notifications | New system notifications, including those from apps such as QQ |
| WeChat | New messages and unread previews from a signed-in desktop client, with conversation avatars when available and support for muted conversations |
| Telegram | New messages from a signed-in desktop client |
| Power | Power connection, charging, disconnection and full-charge events |

The island starts with new messages when a source connects or is re-enabled. Muted WeChat conversations stay quiet, and previous messages are not replayed. Turning off one source leaves the others running.

System notifications require initialization and notification access. Connected sources such as WeChat and the notification preview remain available before system notification setup is complete.

## Settings

The sidebar contains **Setup**, **Sources**, **Plugins**, **Appearance**, **Updates**, and **Diagnostics & feedback**.

| Setting | What you can choose |
| --- | --- |
| Launch at startup | Enable in Setup and approve the Windows administrator prompt to launch in the background when you sign in |
| Game and full-screen mute | Control the two switches independently in Sources; both are enabled by default |
| Windows do not disturb | Toggle in Sources to quiet Windows banners while Windows Island continues receiving messages; Windows priority exceptions still apply |
| Position and animation | Choose a screen position, toggle animation and preview a notification in Appearance |
| Language | Choose Simplified Chinese, English or the system language in Appearance; the app defaults to Simplified Chinese |

When a selected game or full-screen mute condition is detected, the island pauses popups. New messages appear again when it ends, without replaying the backlog. Windows do not disturb is a separate setting.

## Updates

Open **Updates** and click the check icon to view available releases and their notes. Click download to prepare an update; the app restarts to complete installation and keeps your preferences. Interrupted downloads resume from saved progress, while the update page shows the current percentage and transfer rate.

**Include prerelease updates** is off by default. Enable it if you also want to receive prereleases.

## Diagnostics and feedback

In **Diagnostics & feedback**, use the generate icon to create a local diagnostic report. You can preview, copy or export it, control local diagnostic collection, and clear stored records.

Reports are never uploaded automatically and exclude message text, avatars and account information. The feedback icon opens a [GitHub Issue form](https://github.com/Cloudwhile/WindowsIsland/issues/new/choose); you decide whether to attach a report. Issues are public, so review your text and attachments before submitting.

## Building from source

Run from the repository root:

```powershell
dotnet build src/WindowsIsland/WindowsIsland.csproj -c Release -p:Platform=x64
dotnet publish src/WindowsIsland/WindowsIsland.csproj -c Release -p:Platform=x64
```

Published files are placed in `artifacts/publish/`. Copy the entire directory and open `WindowsIsland.exe`.

## License

[Apache License 2.0](LICENSE).
