# HelloNotification

开启一秒后显示一条通知。

需要 Windows、.NET 10 SDK，以及同次 SDK 发行的 CLI 和 Contracts 包。将 nupkg 放到 `C:\wini-sdk-feed`，不需要宿主源码。

```powershell
dotnet restore --source C:\wini-sdk-feed
wini-plugin validate
wini-plugin pack
```

需要独立功能测试时，可参考 `wini-plugin init` 的 notification 模板生成的计划，在本目录编写 `plugin.test.json` 后运行 `wini-plugin test`。
将 `dist/wini_plugin_hellonotification-1.0.0.zip` 导入消息岛并启用，核对界面结果；停用后重新开启应正常工作。
