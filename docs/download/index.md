# 下载消息岛

通过 [GitHub Releases](https://github.com/Cloudwhile/WindowsIsland/releases) 获取消息岛和发布说明。请在所选版本的 **Assets** 中下载对应文件。

## 选择版本

| 渠道 | 下载入口 | 适合的场景 |
| --- | --- | --- |
| 正式版 | [最新正式版](https://github.com/Cloudwhile/WindowsIsland/releases/latest) | 日常使用 |
| 预发布版 | [全部版本](https://github.com/Cloudwhile/WindowsIsland/releases)中带 Pre-release 标记的版本 | 提前体验修复和功能 |

预发布标签通常包含 `alpha`、`beta` 或 `rc`。下载前阅读该版本说明，核对兼容性与升级提示。应用内默认不接收预发布更新。

## 系统要求

- Windows 10 2004（19041）及以上版本，或 Windows 11。
- x64 设备。
- 使用系统通知时，需要允许通知访问；首次初始化可能按需请求管理员授权。

应用发布包已包含所需运行时，正常使用无需另装 .NET SDK。

## 选择下载包

下表中的 `<版本号>` 对应发布标签去掉开头 `v` 后的完整版本，包括预发布后缀。

| 文件 | 用途 |
| --- | --- |
| `WindowsIsland-<版本号>-win-x64.exe` | Windows 安装器，适合常规安装和覆盖升级 |
| `WindowsIsland-<版本号>-win-x64.zip` | 便携包，完整解压后运行 |
| 同名 `.sha256` 文件 | 检查对应安装器或 ZIP 的完整性 |

不同版本的附件可能不同，以该版本 Assets 为准。应用内更新下载安装器；手动使用便携包时，遵循[安装、便携与卸载](./install.md)中的说明。

## 校验下载文件

同时下载所选文件及其同名 `.sha256` 文件。在下载目录打开 PowerShell，将示例文件名替换为实际名称：

```powershell
$file = 'WindowsIsland-<版本号>-win-x64.exe'
$actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
$expected = ((Get-Content -LiteralPath ($file + '.sha256') -Raw).Trim() -split '\s+')[0]
$actual -ieq $expected
```

返回 `True` 表示摘要一致；返回 `False` 时重新下载文件，不继续安装。校验 ZIP 时将 `$file` 改为 ZIP 文件名。

首次安装继续阅读[安装指南](./install.md)，已有版本请看[更新指南](./update.md)。
