# 错误码与排错

CLI 成功退出码为 `0`，失败为 `1`。校验错误尽可能包含文件、JSON 字段路径、错误名称和修复示例；JSON 语法错误还包含行号和字节位置。宿主诊断只保留结构化错误身份，不包含设置值或消息正文。

插件错误的 HResult 为 `0xA0040000 | 数值`，已有数值保持稳定。下表列出所有当前插件错误身份；同组名称按数值一一对应。

| 数值 | 名称 | 定位与修复 |
| --- | --- | --- |
| 1 | `Zip` | ZIP 结构无效，重新 pack，确认未损坏或加密 |
| 2 | `Manifest` | `manifest.json` 或 `settings.schema.json` 结构、字段或默认值无效；按报错路径核对 Schema |
| 3 | `Id` | `$.id` 使用未占用的 `wini_plugin_<name>` |
| 4 | `Contract` | `$.contractVersion` 与目标宿主合约大版本一致，当前为 1 |
| 5 | `Entry` | `$.entryAssembly` 指向包内已有 DLL |
| 6 | `Downgrade` | 版本降级被拒绝，使用兼容的新版本 |
| 7 | `Version` | `$.version` 使用 SemVer，例如 `1.0.0` |
| 8 | `Path` | 包路径必须为相对路径，不能含链接、越界或特殊路径段 |
| 9 | `SourceConflict` | 标识已由其他来源占用，核对来源或先卸载 |
| 10 | `Start` | 初始化或启动失败，查看固定操作名和结构化异常身份 |
| 11 | `SourceAddress` | 目录地址须满足 HTTPS 策略；HTTP 仅允许回环开发地址 |
| 12 | `Hash` | 下载 ZIP 摘要不匹配，重新取得与索引一致的包 |
| 13 | `IndexMismatch` | 索引、包或宿主来源凭据不一致，从原来源重新安装 |
| 14 | `NotListed` | 目标插件或版本不在目录索引中 |
| 15 | `ManagedAssembly` | 入口需为可读取元数据的托管程序集 |
| 16 | `HostDependency` | 移除宿主或 WinUI 依赖，仅引用公共 Contracts |
| 17 | `MissingDependency` | 将非平台托管依赖包含在构建与打包输出中 |
| 18 | `SourceList` | 来源列表结构无效，检查目录协议 |
| 19 | `IndexSize` | 索引超过大小限制，减少条目或元数据 |
| 20 | `IndexDuplicate` | 索引重复标识或版本，移除重复项 |
| 21 | `SourceMetadata` | 来源名称、说明或地址不符合目录协议 |
| 22 | `IndexMetadata` | 插件版本索引字段或安全披露无效 |
| 23 | `SourceEmpty` | 来源没有可用项目 |
| 24 | `DownloadRedirect` | 下载重定向不符合限制，提供允许的实际下载地址 |
| 25 | `SourceSize` | 来源响应过大，缩减响应 |
| 26 | `CacheSize` | 缓存过大或不合法，重新取得有效索引 |
| 27 | `ExpandedSize` | 包解压总量超过 256 MiB，缩减载荷 |
| 28 | `ArchiveLength` | ZIP 文件长度不合法或超过限制 |
| 29 | `ArchiveIncomplete` | ZIP 项读取不完整，重新打包下载 |
| 30 | `IdConflict` | 插件标识已占用，使用唯一标识 |
| 31 | `ManifestSize` | 清单超过 256 KiB，缩减元数据 |
| 32 | `ManifestEmpty` | 清单需为非空 JSON 对象 |
| 33 | `DirectoryId` | 安装目录与清单标识不一致，重新导入 ZIP |
| 34 | `DescriptionSize` | 说明最多 8000 字符，作者最多 200 字符 |
| 35 | `EntryName` | 名称和入口类型不能空白，入口使用完整类型名 |
| 36 | `DeveloperId` | 开发者标识使用 1–64 个合法字符 |
| 37 | `Repository` | 使用 `https://github.com/owner/repository`，无 `.git`、尾斜杠、查询或额外路径 |
| 38 | `Permissions` | 权限与条款、网络披露不匹配，核对对应 JSON 字段 |
| 39 | `PathId` | 路径中的插件标识无效 |
| 40 | `NotReady` | 插件操作时机无效，等待初始化完成或重新启用 |
| 41 | `RuntimeEntry` | 入口必须为公开非抽象类，实现 `IIslandPlugin` 并有公开无参构造函数 |
| 42 | `DuplicateId` | 扫描发现重复标识，移除重复安装来源 |
| 43 | `RuntimeHostDependency` | 运行时拒绝宿主程序集依赖，重新构建独立插件 |
| 44 | `ProxyAddress` | 代理地址不合法，检查用户配置 |
| 45 | `CatalogFetch` | 目录获取失败，检查连接及服务器 |
| 46 | `CatalogStatus` | 目录返回失败 HTTP 状态 |
| 47 | `CatalogJson` | 目录 JSON 无效，核对协议 |
| 48 | `CatalogTimeout` | 目录请求超时，稍后重试 |
| 49 | `CatalogCache` | 目录缓存读写失败，检查本机存储 |
| 50 | `HostVersion` | `$.minimumHostVersion` 或 `--host-version` 无效或目标过旧，使用兼容 SemVer |

`UnauthorizedAccessException`、请求范围无效、两秒频率限制、并发限制或取消不属于上述清单错误码。运行插件应处理服务缺失和请求失败，不能依赖 IPC 恢复原始异常类型；取消后停止工作，授权拒绝后不要改用直接联网。

先执行 `validate`；构建后对解压目录执行 `doctor --host-version 0.1.0`；运行场景使用 `test`。静态检查不执行 DLL，也不能证明服务实际可用。默认插件源已开放，收录状态以客户端 **插件管理 → 发现插件** 列表为准，协议细则见[高级目录协议](./catalog.md)。
