# NetworkStatus

默认关闭请求；实际联网前修改清单和源码中的目的地及用途，再开启 send。

需要 Windows、.NET 10 SDK，以及同次 SDK 发行的 CLI 和 Contracts 包。将 nupkg 放到 `C:\wini-sdk-feed`，不需要宿主源码。

```powershell
dotnet restore --source C:\wini-sdk-feed
wini-plugin validate
wini-plugin pack
```

需要独立功能测试时，可参考 `wini-plugin init` 的 network 模板生成的计划，在本目录编写 `plugin.test.json` 后运行 `wini-plugin test`。模板使用模拟响应，不代表实际服务器可用。
将 `dist/wini_plugin_networkstatus-1.0.0.zip` 导入消息岛并启用，核对界面结果；停用后重新开启应正常工作。
