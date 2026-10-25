# 创建第一个插件

本例在每次开启插件一秒后发送一条演示消息。它使用宿主上下文提交通知，并提供一个文本设置和一个开关设置。

## 准备项目

安装 .NET 10 SDK，克隆[消息岛仓库](https://github.com/Cloudwhile/WindowsIsland)，在仓库根目录执行：

```powershell
dotnet new classlib -n Example.Notifications -o artifacts/plugin-example -f net10.0
dotnet add artifacts/plugin-example/Example.Notifications.csproj reference src/WindowsIsland.Contracts/WindowsIsland.Contracts.csproj
```

也可以在独立仓库创建类库，引用与目标宿主对应的 `WindowsIsland.Contracts.dll`。无需引用消息岛主程序集或 WinUI。

将生成的 `Class1.cs` 替换为以下内容。入口是公开的非抽象类，保留公开无参数构造函数。

## 实现入口

```csharp
using WindowsIsland.Contracts;

namespace Example.Notifications;

public sealed class ExamplePlugin : IIslandPlugin, IPluginSettingsSchema
{
    private IPluginContext? _context;
    private CancellationTokenSource? _shutdown;
    private Task? _run;

    public string Id => "wini_plugin_example_notifications";
    public string DisplayName => "示例通知";

    public IReadOnlyList<PluginSetting> SettingsSchema =>
    [
        new("message", PluginSettingType.String, "消息内容", "你好，消息岛"),
        new("enabled", PluginSettingType.Boolean, "发送演示消息", true)
    ];

    public Task InitializeAsync(IPluginContext context)
    {
        _context = context;
        return Task.CompletedTask;
    }

    public Task StartAsync()
    {
        if (_run is not null) return Task.CompletedTask;
        var context = _context ?? throw new InvalidOperationException("插件尚未初始化");
        _shutdown = new CancellationTokenSource();
        _run = SendAsync(context, _shutdown.Token);
        return Task.CompletedTask;
    }

    private async Task SendAsync(IPluginContext context, CancellationToken token)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), token);
        token.ThrowIfCancellationRequested();
        if (!context.Settings.GetBoolean("enabled", true)) return;
        context.PublishNotification(new PluginNotification(
            SourceId: Id,
            ConversationName: "示例通知",
            Sender: null,
            Body: context.Settings.GetString("message", "你好，消息岛")!,
            CreatedAt: DateTimeOffset.UtcNow,
            DeduplicationKey: Guid.NewGuid().ToString("N"),
            AppName: DisplayName));
    }

    public async Task StopAsync()
    {
        var shutdown = _shutdown;
        var run = _run;
        _shutdown = null;
        _run = null;
        shutdown?.Cancel();
        try
        {
            if (run is not null) await run;
        }
        catch (OperationCanceledException) when (shutdown?.IsCancellationRequested == true)
        {
        }
        finally
        {
            shutdown?.Dispose();
        }
    }

    public void Dispose()
    {
        _shutdown?.Cancel();
        _shutdown?.Dispose();
        _shutdown = null;
        _context = null;
    }
}
```

`StartAsync` 返回的是启动完成，不是后台任务的整个生命周期。实际消息来源应订阅新事件或建立监听基线，停止时等待后台任务结束并释放订阅。

## 添加清单

在 `artifacts/plugin-example/manifest.json` 写入：

```json
{
  "id": "wini_plugin_example_notifications",
  "name": "示例通知",
  "version": "1.0.0",
  "entryAssembly": "Example.Notifications.dll",
  "entryType": "Example.Notifications.ExamplePlugin",
  "contractVersion": 1,
  "permissions": ["notifications"],
  "author": "示例作者",
  "description": "开启后发送一条可配置的演示消息。"
}
```

此处的 `id` 与类中的 `Id` 完全一致。`entryType` 包含命名空间，`entryAssembly` 与类库输出文件名一致。

## 编译与打包

在仓库根目录执行以下命令；输出路径适用于上面的项目结构：

```powershell
dotnet build artifacts/plugin-example/Example.Notifications.csproj -c Release -o artifacts/plugin-example/build
$stage = 'artifacts/plugin-example/package'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item artifacts/plugin-example/build/Example.Notifications.dll $stage
Copy-Item artifacts/plugin-example/manifest.json $stage
Compress-Archive -Path "$stage/*" -DestinationPath artifacts/plugin-example/Example.Notifications-1.0.0.zip -Force
```

ZIP 根目录应直接出现 `manifest.json` 和 `Example.Notifications.dll`。有额外依赖时一并打包，详见[打包指南](./packaging.md)。

## 安装与验证

1. 在开发用消息岛的 **插件管理** 页通过安装图标导入生成的 ZIP。
2. 在 **消息来源** 页找到 **示例通知**，开启来源并核对启用确认中的身份和版本；有条款或联网须知时先阅读并确认。
3. 一秒后应收到一条通知。点击名称进入详情修改消息内容，再停用并启用以查看修改后的通知。
4. 关闭再开启，确认只产生一条新演示消息。
5. 开启后立即停用，确认被取消的旧任务不会恢复通知。

应用处于游戏或全屏静音时，真实通知可能被丢弃。示例不会自动重新发送，不需要重复初始化系统通知来调试它。
