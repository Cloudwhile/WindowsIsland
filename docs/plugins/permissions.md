# 权限与诊断

## 权限模型

外部插件安装后默认关闭。首次启用时，用户先核对插件身份和版本；插件提供使用条款时，必须阅读并明确同意。声明联网时，还需确认网络目的地、用途和数据类别。取消确认后保持关闭。

授权与当前插件包及披露内容绑定。更新、同版本覆盖，或条款和联网说明改变后，需要重新确认。应用启动、重新扫描和重试不会代替用户同意；官方作者与第三方作者遵循相同的确认流程。

外部插件只能使用自身的设置和数据，不能读取消息岛本体或其他插件的数据。联网通过 `IPluginNetwork` 提交，启用时一次性确认联网须知；不要直接创建 `HttpClient`、套接字或使用其他网络接口。

## 已定义权限

| 名称 | 用途 | 宿主实际处理 |
| --- | --- | --- |
| `notifications` | 提交通知 | 控制宿主通知投递 |
| `settings` | 插件数据目录 | 提供自身 `IPluginStorage` 服务，不关闭 `IPluginSettings` |
| `process` | 点击时激活进程 | 外部插件不授予该能力 |
| `network` | 经确认的网络请求 | 启用时确认开发者或默认插件源提供的联网须知 |
| `filesystem` | 数据目录外文件访问 | 外部插件不授予该能力 |
| `native` | 原生代码访问 | 外部插件不授予该能力 |
| `automation` | 辅助访问 | 外部插件不授予该能力 |

未知权限不授予。普通通知能力和数据目录能力仍需要在清单中声明；外部插件不能通过手动填写来源或官方作者信息扩大权限。

```csharp
var permissions = context.GetService<IPluginPermissions>();
var canActivateProcess = permissions?.IsGranted(PluginPermissions.Process) == true;
```

可选服务可能返回 `null`。按实际能力降级，避免因缺少点击激活能力而停止普通消息功能。

## 网络请求与云端发送

使用 `network` 权限前，必须提供开发者或默认插件源的联网须知，格式见[清单与标识](./manifest.md#条款与联网须知)。例如，插件将用户在插件内创建的便签发送至自己的同步服务：

```csharp
using System.Text.Json;
using WindowsIsland.Contracts;

var network = context.GetService<IPluginNetwork>();
if (network is null) return;

var response = await network.SendAsync(new PluginNetworkRequest(
    Url: "https://api.example.com/notes",
    Method: "POST",
    Purpose: "同步用户在此插件中创建的便签",
    Headers: new Dictionary<string, string> { ["Content-Type"] = "application/json" },
    Body: JsonSerializer.SerializeToUtf8Bytes(new { text = "用户选择同步的便签" })),
    cancellationToken);
```

`Purpose` 必须与清单披露一致，地址必须位于已披露的 HTTPS 目的地下，使用标准端口 443；不接受局域网、回环或保留地址。允许的方法为 `GET`、`HEAD`、`POST`、`PUT`、`PATCH`、`DELETE`。请求正文最多 1 MiB，响应正文最多 2 MiB；请求头、地址和用途也有上限，见 `PluginNetworkLimits`。

联网须知由开发者在清单中提供；包内缺失时，默认插件源可通过版本索引的 `network` 字段补充。开发者声明优先，其他源不能提供此后备声明。启用时确认一次，之后不再逐次弹窗；内容或包变化后需要重新确认。宿主不附加自己的凭据，不允许重定向扩大已确认的范围。

用户必须保持当前设置窗口可见并自行确认。设置未打开、切到后台、关闭或取消时，请求不会发送；插件应处理拒绝与取消，不得反复弹窗或自动改用其他发送方式。不要把消息岛本体、其他插件的数据或宿主凭据加入请求。

## 结构化诊断

使用当前插件绑定的诊断入口，传入固定操作名与结构化元数据：

```csharp
context.Diagnostics.Write(new PluginDiagnostic(
    Area: context.PluginId,
    Operation: "Refresh",
    AdditionalInformation: new PluginDiagnosticInfo(
        ExceptionType: typeof(IOException).FullName,
        HResult: unchecked((int)0x80070020),
        ClientVersion: "1.2.3")));
```

宿主会绑定真实插件标识与诊断区域。操作名应为稳定标识，不放入消息正文、账号、地址、凭据或文件路径。附加信息支持异常类型、数值 HResult 和仅含数字与点的客户端文件版本。

不要传递异常 `Message`、`Data`、`Source`、原始堆栈和自由文本。诊断采集开关、合并计数、保留期限和导出由宿主控制。

## 调试失败

1. 在消息来源行查看结果与重试入口。
2. 核对清单标识、入口、合约和依赖。
3. 在诊断与反馈页查看相应记录并导出报告。
4. 在开发环境修复后重新编译并导入。

本机报告不会自动上传；公开反馈前检查报告和附件。宿主诊断不能替代插件对后台任务异常和取消的正常处理。
