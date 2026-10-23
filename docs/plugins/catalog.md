# 目录接口契约

开放平台目前尚未开放。
本文定义目录服务与消息岛宿主之间的接口；手动 ZIP 分发见[打包与分发](./packaging.md)。

## 基地址与请求

| 编号 | 约定 |
| --- | --- |
| C1 | 宿主使用应用提供的固定官方目录。 |
| C2 | 目录基地址为绝对 HTTPS 地址，不包含 userinfo、query 或 fragment。 |
| C3 | 其他来源不能替代官方目录。 |
| C4 | 仅复用同一官方目录的索引缓存。 |
| C5 | 端点路径精确、区分大小写；基地址末尾去重后保留一个 `/`，请求不带 query，不跟随 3xx。 |
| C6 | 请求带 `Cache-Control: no-cache`；只有 HTTP 200 成功，304 及其他状态均表示源不可用。 |

```http
GET {base}/api/sources
GET {base}/api/sources/{developerId}/plugins
```

这里的 `{base}` 指去除末尾斜杠后的基地址。下方使用 `catalog.example` 演示结构。

## 传输与格式

| 编号 | 约定 |
| --- | --- |
| C7 | 响应必须是 UTF-8 JSON 数组，空数组合法；任意层级重复 JSON 键使整个响应无效。 |
| C8 | 每个响应体最多 4 MiB；超限整体拒绝，不使用部分数据。 |
| C9 | 不以 Content-Type 判断合法性；JSON 解析失败即拒绝，不猜测格式。 |
| C10 | 每个请求超时 45 秒；校验失败或瞬时网络错误均不在本轮重试，下次刷新再请求。 |
| C11 | 并发目录刷新合并为一次，不并行拉取同一源。 |
| C12 | 目录请求匿名，不发送凭据、token 或用户标识头。 |

来源列表示例：

```json
[
  {
    "developerId": "winiofficial",
    "developerName": "winiofficial",
    "repositoryUrl": "https://github.com/example/plugins",
    "pluginsUrl": "https://catalog.example/api/sources/winiofficial/plugins"
  }
]
```

`example/plugins` 仅演示地址结构。实际条目必须使用自己的公开仓库，并与下载附件保持一致。

## 字段校验

一个响应中的任意来源或插件字段无效，整个对应响应无效，不跳过坏条目后使用剩余内容。

| 编号 | 字段与约定 |
| --- | --- |
| C13 | `developerId` 匹配 `^[a-z][a-z0-9_]*$`，最长 64 字符，全局唯一；官方身份是 `winiofficial`。 |
| C14 | `developerName` 非空、最长 200；`repositoryUrl` 精确为 `https://github.com/{owner}/{repo}`，两段各匹配 `^[A-Za-z0-9_.-]+$`，无尾斜杠、`.git` 后缀或额外路径段。 |
| C15 | `pluginsUrl` 逐字等于当前基地址下的 `/api/sources/{developerId}/plugins`。 |
| C16 | 来源最多 512 个，每源最多 2048 个插件，全目录最多 8192 个插件；超限拒绝。 |
| C17 | `id` 为 `wini_plugin_<name>`，name 匹配 `^[a-z][a-z0-9_]*$`，完整标识最长 80；全目录唯一，不得占用内置保留标识，包括 `wini_plugin_countdown`。 |
| C18 | 插件的 `developerId`、`repositoryUrl` 与所属来源逐字一致。 |
| C19 | `name` 非空、最长 200；`description` 必须存在，可为空字符串，最长 8000。 |
| C20 | `version`、`minHostVersion` 为严格 SemVer 2.0，数字标识不得有前导零；比较忽略 `+` 后的构建元数据。 |
| C21 | `contractVersion` 是至少为 1 的 JSON 整数；`1.0`、`1e0`、`"1"` 均无效。 |
| C22 | `permissions` 是最多 64 项的数组，每项非空、最长 80、匹配 `^[a-z][a-z0-9_]*$`，不得重复；未知权限使条目无效。 |
| C23 | `sha256` 必须由服务端发送为 64 位小写十六进制；下载校验比较时双方统一小写，大小写差异不导致校验失败。 |
| C24 | `downloadUrl` 是 HTTPS、域名恰为 `github.com`、默认端口，无 userinfo、query 或 fragment；路径精确为对应仓库的 `/releases/download/{tag}/{file}.zip`。tag 和附件名各为单个非空段，不含 `..`、编码分隔符（包括 `%2F`、`%5C`）或额外路径段。 |

当前已知权限为 `notifications`、`settings`、`process`、`network`、`filesystem`、`native`、`automation`，
含义见[权限与诊断](./permissions.md)。

索引条目示例：

```json
[
  {
    "id": "wini_plugin_example",
    "name": "示例插件",
    "version": "1.0.0",
    "downloadUrl": "https://github.com/example/plugins/releases/download/v1.0.0/example.zip",
    "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
    "minHostVersion": "0.1.0",
    "contractVersion": 1,
    "permissions": ["notifications", "settings"],
    "description": "",
    "repositoryUrl": "https://github.com/example/plugins",
    "developerId": "winiofficial"
  }
]
```

示例摘要需替换为实际 ZIP 的 SHA-256。PowerShell 可输出小写摘要：

```powershell
(Get-FileHash ./example.zip -Algorithm SHA256).Hash.ToLowerInvariant()
```

## 版本与兼容性

| 编号 | 约定 |
| --- | --- |
| C25 | 最低宿主版本高于当前版本，或合约大版本与宿主不同（当前为 1），仅排除安装与更新候选；索引仍然有效，不记录错误诊断。 |
| C26 | 更新候选必须是同 id、通过全部校验、兼容且按 SemVer 严格高于已安装版本的条目；构建元数据不影响比较。 |
| C27 | 默认稳定渠道只提供没有预发布部分的版本；开启更新页的预发布选项后才提供预发布版本，稳定渠道下不会用预发布版替换正式版。 |
| C28 | 冻结由平台保持索引版本不再前进实现；保留的冻结版本仍可供新安装，宿主无需额外动作。 |
| C29 | 索引版本低于已安装版本时不提供动作、提示或回滚，永不降级。 |

## 下架与缓存

| 编号 | 约定 |
| --- | --- |
| C30 | 仅两种成功观察可判定下架：该开发者本轮索引完整合法且缺少已安装 id；或此前出现过的开发者在本轮合法来源列表中消失。拉取失败、校验失败、从未出现过的源和旧缓存均不构成下架证据。 |
| C31 | 下架只在详情中警告、排除更新；已安装插件继续运行，不自动卸载或禁用。 |
| C32 | 每源保留最近完整合法的索引；新索引通过全部校验后原子替换。网络、状态码、解析或校验失败完整保留旧缓存；缓存没有过期时间，旧缓存本身不触发下架判定。 |
| C33 | `winiofficial` 缺席不是错误；空官方目录合法，表示暂无官方插件。 |

## 诊断与安装校验

| 编号 | 约定 |
| --- | --- |
| C34 | 目录失败使用稳定错误码，覆盖基地址、拉取、HTTP 状态、解析、来源、索引、条目、校验和、大小与超时；界面按码翻译，不按异常文本匹配。 |
| C35 | 安装前核对 ZIP 的 SHA-256，包内清单 id 和版本必须与索引一致；失败拒绝安装，保留既有插件、数据与备份，不留下半安装状态。 |
| C36 | 只有 `developerId == winiofficial` 获官方身份、完整声明权限和官方作者展示；其他 id 均为三方，高危权限逐项确认，包内自称官方无效。 |

宿主在暂存目录检查插件包，再安装或替换；更新保留上一份代码备份，失败恢复原安装。
目录来源与 GitHub 下载代理分别配置，代理不会改变索引身份或跳过完整性校验。
