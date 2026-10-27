# 权限与诊断

## 权限模型

外部插件安装后默认关闭。首次启用时，用户先核对插件身份和版本；插件提供使用条款时，必须阅读并明确同意。声明联网时，还需确认网络目的地、用途和数据类别。取消确认后保持关闭。

授权与当前插件包及披露内容绑定。更新、同版本覆盖，或条款和联网说明改变后，需要重新确认。应用启动、重新扫描和重试不会代替用户同意；官方作者与第三方作者遵循相同的确认流程。

常规存储限插件自身的设置和数据，不能读取消息岛本体或其他插件的数据。经专门来源核验的桌面适配器可通过宿主访问声明的客户端，详见[桌面客户端代理](./desktop.md)。联网通过 `IPluginNetwork` 提交，启用时一次性确认联网须知；不要直接创建 `HttpClient`、套接字或使用其他网络接口。

## 外部插件可申请的权限

| 名称 | 用途 | 宿主实际处理 |
| --- | --- | --- |
| `notifications` | 提交通知与固定内容 | 控制通知投递及 `PinnedContent` 提交 |
| `settings` | 插件数据目录 | 提供自身 `IPluginStorage` 服务，不关闭 `IPluginSettings` |
| `network` | 经确认的网络请求 | 启用时确认开发者或默认插件源提供的联网须知 |

### 内置或保留能力

| 名称 | 用途 | 宿主实际处理 |
| --- | --- | --- |
| `process` | 客户端模块与点击目标 | 仅经来源及范围核验的桌面适配器可使用宿主代理；其他外部插件不授予 |
| `filesystem` | 数据目录外文件访问 | 外部插件不授予该能力 |
| `native` | 原生代码访问 | 外部插件不授予该能力 |
| `automation` | 声明客户端的辅助访问 | 仅经来源及范围核验的桌面适配器可使用 `IPluginDesktop`，不授予直接桌面访问 |

平台允许开发者登记不重名的自定义权限标签，目录与清单接受格式合法的标签；标签本身不增加宿主能力，未知或未支持的能力不授予。普通通知能力和数据目录能力仍需要在清单中声明；外部插件不能通过手动填写来源或官方作者信息扩大权限。

### 官方微信适配器

微信逻辑由独立插件实现，使用 `notifications`、`settings`、`automation` 和 `process`，并在 `desktop` 声明客户端范围。宿主同时核对默认插件源安装凭据、插件标识 `wini_plugin_wechat`、开发者 `winiofficial` 和仓库 `https://github.com/WinIOfficial/wini_plugin_wechat`。手动导入或复制官方字段不能取得桌面代理。

插件在独立 LPAC 工作进程中解析微信窗口、消息、头像及免打扰状态；宿主只提供有界桌面与模块操作，并核对实际点击目标。停用、失败、升级或卸载后撤销代理。旧 `wechat` 标签和 `IWeChatHost` 类型仅保留历史兼容性，当前宿主不再授予专用监听能力；不提供直接进程、桌面或联网权限。

`context.Settings` 的字符串和布尔设置无需 `settings` 权限；该权限只控制独立数据目录服务 `IPluginStorage`。`native` 未获授予不表示禁止执行所有机器指令或 Win32 API，资源访问边界由操作系统沙箱实施。

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

public static class NoteSync
{
    public static async Task<bool> SendAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var network = context.GetService<IPluginNetwork>();
        if (network is null) return false;
        try
        {
            var response = await network.SendAsync(new PluginNetworkRequest(
                Url: "https://api.example.com/notes",
                Method: "POST",
                Purpose: "同步用户在此插件中创建的便签",
                Headers: new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                Body: JsonSerializer.SerializeToUtf8Bytes(new { text = "用户选择同步的便签" })),
                cancellationToken);
            return response.StatusCode is >= 200 and < 300;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }
}
```

`Purpose` 必须与清单披露一致，地址必须位于已披露的 HTTPS 目的地下，使用标准端口 443；不接受局域网、回环或保留地址。允许的方法为 `GET`、`HEAD`、`POST`、`PUT`、`PATCH`、`DELETE`。请求正文最多 1 MiB，响应正文最多 2 MiB；请求头、地址和用途也有上限，见 `PluginNetworkLimits`。

联网须知由开发者在清单中提供；包内缺失时，默认插件源可通过版本索引的 `network` 字段补充。开发者声明优先，其他源不能提供此后备声明。启用时确认一次，之后不再逐次弹窗；内容或包变化后需要重新确认。宿主不附加自己的凭据，不允许重定向扩大已确认的范围。

授权后可在已披露范围内后台联网，无需保持设置窗口打开。停用、撤销授权或包变化后，请求会被拒绝；插件应处理拒绝与取消，不得自动改用其他发送方式。不要把消息岛本体、其他插件的数据或宿主凭据加入请求。

数据类别是开发者对可能传输内容的声明。宿主限制目的地、用途标识和传输规模，但不能自动识别正文含义或阻止所有敏感内容，仍需核查开发者及其实现。

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
