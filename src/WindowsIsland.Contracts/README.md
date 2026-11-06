# WindowsIsland.Contracts

消息岛插件的独立 .NET 10 合约包，不依赖 WinUI 或宿主程序集。包版本随 SDK 发布，`AssemblyVersion` 保持 `1.0.0.0`，插件清单的 `contractVersion` 为 `1`。

```powershell
dotnet add package WindowsIsland.Contracts --source C:\wini-sdk-feed
```

使用与目标宿主同次发布的 SDK；可选服务通过 `IPluginContext.GetService<T>()` 查询并处理不可用情况。入口实现 `IIslandPlugin`，已有事件通知模型可实现其派生接口 `IMessageSource`。

打包插件时不要附带此合约 DLL，由宿主提供合约。`wini-plugin pack` 自动完成排除。

文档：https://github.com/Cloudwhile/WindowsIsland/tree/master/docs/plugins
