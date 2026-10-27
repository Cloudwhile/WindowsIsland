# WindowsIsland.Plugin.Cli

Windows / .NET 10 插件开发工具。安装获得的 nupkg 后可使用：

```powershell
wini-plugin init HelloIsland --template notification
cd HelloIsland
wini-plugin validate
wini-plugin test
wini-plugin pack
```

模板支持 `notification`、`pinned`、`network`，生成项目包含独立的合约引用，不需要克隆宿主仓库。网络模板默认关闭示例请求。

`validate` 校验项目清单和静态设置；`build` 构建 Release 并检查完整输出的入口及依赖；`pack` 生成 ZIP 和 SHA-256，拒绝覆盖已有 ZIP。`doctor <解压目录>` 检查 .NET SDK 版本及包内入口、公开无参数构造函数和依赖。静态检查不执行插件 DLL。

`test` 根据项目的 `plugin.test.json` 启动独立 Worker，使用临时数据和模拟网络，检查通知数量、固定控件交互和停止行为。测试报告在 `.wini/tests/`。它不显示应用界面，也不读取正式插件配置。

将 ZIP 导入消息岛并启用，按披露确认授权后查看实际通知或固定内容。
