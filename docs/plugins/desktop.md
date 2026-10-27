# 桌面客户端代理

`IPluginDesktop` 是可选宿主服务。插件仍在 LPAC 工作进程中运行，客户端解析、消息判断和头像策略由插件实现；宿主仅对清单声明的客户端执行有界桌面操作，不加载插件程序集。

## 来源与声明

目前仅默认插件源验证的官方微信适配器可获得此服务。宿主核对安装来源凭据、开发者、仓库、插件标识及包完整性，并要求 `notifications`、`automation`、`process` 声明。手动包、自称官方或仅登记权限标签均不能取得能力。需要保存头像缓存时，另声明 `settings` 并使用自身 `IPluginStorage`。

在清单中声明访问范围：

```json
{
  "permissions": ["notifications", "settings", "automation", "process"],
  "desktop": {
    "processes": ["Weixin", "WeChat"],
    "modules": ["Weixin.dll"],
    "purpose": "读取微信的新消息、会话免打扰状态和头像，并按需开启辅助访问。"
  }
}
```

`processes` 和 `modules` 各为 1 至 8 项，名称最长 80 字符，忽略大小写后不能重复。进程名不带扩展名，仅使用 ASCII 字母、数字、下划线或连字符；模块名可含点并以 `.dll` 结尾，不含路径或 `..`。用途非空且最长 1000 字符。仍需提供完整使用条款，说明所读取的客户端信息与辅助访问行为，启用前由用户确认。

## 查询与快照

先查询服务是否可用：

```csharp
using WindowsIsland.Contracts;

public static class DesktopExample
{
    public static async Task<int> CountClientsAsync(IPluginContext context, CancellationToken token)
    {
        var desktop = context.GetService<IPluginDesktop>();
        if (desktop is null) return 0;
        return (await desktop.FindClientsAsync(token)).Length;
    }
}
```

`FindClientsAsync` 只返回声明范围内的客户端；当前宿主和插件运行目录下的进程被排除。后续请求重新核对进程创建时间及窗口归属，不允许指定其他进程或任意窗口。

`ReadTreeAsync` 返回平面节点数组，`Parent` 是父节点下标。快照包含名称、控件类型、运行时身份和坐标，不包含原生对象或可继承句柄；密码控件不返回名称。每个快照最多 2048 个节点、512 Ki 字符文本，读取时间有界。图像令牌仅在当前服务实例有效，最多保留 8 份、有效期 5 秒，读取新快照会替换同窗口的旧令牌。

`ReadImageAsync` 只能使用仍有效的快照元素。引用图像限客户端程序目录内的本地文件，拒绝网络路径和链接；屏幕捕获限该窗口及元素内部的 20 至 256 像素区域。捕获前后重新核对元素身份与坐标，复用或重排后的旧行不能提供头像。结果为 64 × 64 PNG，最小化或不稳定的窗口可返回空结果，插件应使用已确认的自身缓存。

## 客户端模块

`ReadModuleAsync` 与 `ReadMemoryAsync` 只读取已声明、已发现模块内的区间，单次最多 1 MiB，不接受任意内存地址。模块匹配、指令识别及客户端版本判断在插件中完成。

`SetFlagAsync` 仅设置模块内可写映像区的一字节布尔值，拒绝非布尔原值、可执行区和不匹配的锚点。每次写入前核对插件提供的有界模块片段；宿主记录本次由 0 改为 1 的位置，撤销时只有模块仍匹配、当前值仍为 1 且系统屏幕阅读器未启用才恢复。插件不得把此接口当作任意内存写入。

## 通知与停用

插件通过 `PublishNotification` 提交自己解析的消息。点击目标只保留宿主核对仍属于该桌面服务的客户端进程，其他 PID 被移除。

实现 `IPluginConversationPolicy` 可提供最多 1024 项会话状态，供宿主过滤同一客户端的其他来源通知；插件不会接收那些来源的消息正文。状态绑定当前运行代次并有时限，未知或过期的微信免打扰状态保持静默。

停用、失败、替换或卸载会立即撤销代理，取消排队请求并等待原工作进程及资源释放。插件停止时取消后台任务，迟到结果不能重新提交通知或恢复旧状态。`IWeChatHost` 只保留旧版 ABI 类型，当前宿主不再提供该服务。
