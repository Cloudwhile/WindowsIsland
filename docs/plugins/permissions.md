# 权限与诊断

## 权限模型

插件在消息岛进程内运行，与宿主拥有相同的系统权限。程序集加载隔离有助于分离依赖和卸载，不是安全沙箱。

清单中的权限声明用于说明能力需求、官方目录审核和安装确认。不要把未获授某个宿主能力理解为直接调用对应系统 API 会被阻止。

## 已定义权限

| 名称 | 用途 | 宿主实际处理 |
| --- | --- | --- |
| `notifications` | 提交通知 | 控制宿主通知投递 |
| `settings` | 插件数据目录 | 控制 `IPluginStorage` 服务，不关闭 `IPluginSettings` |
| `process` | 点击时激活进程 | 未获授时剥离通知进程号并阻止宿主执行该激活 |
| `network` | 网络访问声明 | 无法阻止插件直接调用网络 API |
| `filesystem` | 数据目录外文件访问声明 | 无法阻止插件直接调用文件 API |
| `native` | 原生代码声明 | 无法阻止插件直接调用原生 API |
| `automation` | 辅助访问声明 | 无法阻止插件直接调用自动化 API |

后五项属于高危权限。手动来源的高危和未知权限不授予；官方来源的高危权限也需要逐项确认。普通通知能力和数据目录能力仍需要在清单中声明。

```csharp
var permissions = context.GetService<IPluginPermissions>();
var canActivateProcess = permissions?.IsGranted(PluginPermissions.Process) == true;
```

可选服务可能返回 `null`。按实际能力降级，避免因缺少点击激活能力而停止普通消息功能。

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
