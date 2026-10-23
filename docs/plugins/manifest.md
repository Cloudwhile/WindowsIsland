# 清单与标识

每个插件 ZIP 根目录都包含一个 `manifest.json`。宿主先验证清单与程序集元数据，再创建插件入口。

## 标识格式

```text
wini_plugin_<name>
```

`wini_plugin_` 是固定小写前缀，`<name>` 替换为实际名称，不保留括号或尖括号：

- 以小写英文字母开头。
- 仅包含小写英文字母、数字及分隔用的单个下划线。
- 不以底线结束，不包含连续下划线。
- 完整标识最长 80 字符，区分大小写。

| 有效示例 | 无效示例 | 原因 |
| --- | --- | --- |
| `wini_plugin_example` | `wini_example_plugin` | 旧的前后缀排列 |
| `wini_plugin_weather2` | `wini_plugin_Weather` | 包含大写字母 |
| `wini_plugin_my_source` | `wini_plugin_2source` | 名称以数字开头 |
| `wini_plugin_example_notifications` | `wini_plugin_my__source` | 连续下划线 |

内置标识 `wini_plugin_system`、`wini_plugin_wechat`、`wini_plugin_telegram`、`wini_plugin_power`，以及停用来源的保留标识 `wini_plugin_countdown`，不能由外部插件占用。

插件入口的 `Id`、清单的 `id` 和提交通知时的 `SourceId` 必须相同。不要将插件版本加入标识，同一个插件更新时保持标识稳定。

## 清单示例

```json
{
  "id": "wini_plugin_example_notifications",
  "name": "示例通知",
  "version": "1.0.0",
  "entryAssembly": "Example.Notifications.dll",
  "entryType": "Example.Notifications.ExamplePlugin",
  "contractVersion": 1,
  "permissions": ["notifications", "settings"],
  "author": "示例作者",
  "description": "接收示例来源的新消息。",
  "developerId": "example_studio",
  "developerName": "示例工作室"
}
```

## 字段说明

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `id` | 是 | 全局唯一的规范标识 |
| `name` | 是 | 非空显示名称，最长 200 字符 |
| `version` | 是 | SemVer 版本，例如 `1.0.0`、`1.1.0-alpha.1` |
| `entryAssembly` | 是 | 相对插件目录的入口程序集路径 |
| `entryType` | 是 | 公开入口类的完整名称 |
| `contractVersion` | 是 | 合约大版本，当前为 `1` |
| `permissions` | 是 | 不重复的权限数组，不需要时用 `[]` |
| `author` | 否 | 作者名称，最长 200 字符 |
| `description` | 否 | 插件说明，最长 8000 字符 |
| `developerId` | 否 | 开发者标识，省略时为空字符串 |
| `developerName` | 否 | 开发者显示名称 |
| `repositoryUrl` | 否 | 公开 GitHub 仓库的 HTTPS 地址 |

清单最大 256 KiB。路径使用 `/` 分隔，不能包含绝对路径、`..`、反斜线或重解析点。

开发者标识使用与名称相同的小写规则，最长 64 字符，不带 `wini_plugin_` 前缀。`repositoryUrl` 应为 `https://github.com/owner/repository` 形式，不带查询参数或片段。

## 入口与依赖

入口必须是实现 `IIslandPlugin` 或 `IMessageSource` 的公开非抽象类，且具有公开无参数构造函数。构造函数只准备本地状态，初始化和监听分别放在生命周期方法中。

仅引用公共 Contracts，不依赖宿主主程序集。所有非平台托管依赖都应随包提供；宿主共享自身的 Contracts 实例。

## 来源归属

`PluginManifest` 也包含 `SourceKind`，但安装器和扫描器按实际安装来源设置它。手动填写 `sourceKind` 无法获得官方身份、权限或自定义面板资格。

旧标识的迁移步骤见[打包、分发与迁移](./packaging.md#迁移旧标识)。
