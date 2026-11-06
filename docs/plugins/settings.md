# 静态设置与敏感值

将 `settings.schema.json` 放在包根目录，用户即可在首次启用前填写配置。宿主只读取 JSON，不运行 DLL；静态声明损坏时拒绝安装或显示配置，不回退执行动态代码。

## 版本 2

```json
{
  "schemaVersion": 2,
  "fields": [
    { "key": "endpoint", "type": "url", "title": "服务地址", "required": true },
    { "key": "token", "type": "password", "title": "访问令牌", "required": true },
    { "key": "interval", "type": "integer", "title": "刷新间隔", "min": 10, "max": 3600, "default": 60 },
    { "key": "mode", "type": "enum", "title": "同步模式", "choices": ["manual", "automatic"], "default": "manual" },
    { "key": "enabled", "type": "boolean", "title": "接收消息", "default": true }
  ]
}
```

| 类型 | 读取和写入 | 校验 |
| --- | --- | --- |
| `string` | `GetString` / `SetString` | 最长 4096 字符 |
| `boolean` | `GetBoolean` / `SetBoolean` | 必须提供布尔默认值 |
| `integer` | `GetString` / `SetString` | Int64 十进制字符串；JSON 默认值是整数；`min`、`max` 包含边界 |
| `enum` | `GetString` / `SetString` | `choices` 必填，1–64 个不重复非空字符串，精确匹配大小写 |
| `url` | `GetString` / `SetString` | 绝对 HTTPS URL，禁止内嵌用户名或密码 |
| `password` | `GetString` / `SetString` | 无默认值；宿主使用当前 Windows 用户的 DPAPI 加密保存 |

版本 2 中，`string` 的 `secret: true` 等同于 `password`。秘密值不预填到编辑控件，不提供明文预览。秘密字段禁止出现 `default`，包括 `null`。插件读取明文后仍有责任保护它；DPAPI 不阻止同一 Windows 用户或本机管理员的主动访问，也不将密码授权给其他插件。

`required: true` 要求非空值；尚未填写时用户可继续预配置，宿主在初始化前阻止启动。可选字段允许空值；`SetString(key, null)` 删除可选值。URL 校验不授予联网能力，实际请求仍须满足清单披露和网络授权。

每份声明最多 64 KiB、64 个字段；键最长 128 字符且不重复，禁止 `Host.` 前缀；标题最长 200 字符，选项最长 200 字符。拒绝未知字段、重复 JSON 属性、错误类型及链接文件。机器可读定义见 [JSON Schema](/schemas/plugin-settings.schema.json)，宿主还会执行默认值、上下界关系和文件安全校验。

## 变更通知

插件可额外实现 `IPluginSettingsChanged`。回调仅包含已经保存的键；重新读取所需设置，不把值加入日志。宿主可合并变化，插件不能依赖每次键入都有一次回调。回调串行执行，超过 15 秒或抛出异常会使当前实例失败；较长操作应交给可取消的后台任务。

```csharp
using WindowsIsland.Contracts;

public sealed class SettingsAwarePlugin : IIslandPlugin, IPluginSettingsChanged
{
    private IPluginContext? _context;
    private string? _endpoint;
    public string Id => "wini_plugin_settingsaware";
    public string DisplayName => "Settings aware";
    public Task InitializeAsync(IPluginContext context)
    {
        _context = context;
        ReadSettings();
        return Task.CompletedTask;
    }
    public Task OnSettingsChangedAsync(IReadOnlyList<string> keys)
    {
        if (keys.Contains("endpoint")) ReadSettings();
        return Task.CompletedTask;
    }
    private void ReadSettings() => _endpoint = _context!.Settings.GetString("endpoint");
    public Task StartAsync() => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public void Dispose() { _context = null; _endpoint = null; }
}
```

停用状态可修改静态设置，下一次初始化读取新值；停用、失败、更新或删除后，旧实例不再接收排队变更。设置写入不会启用插件或授予网络许可。

## 兼容旧插件

`schemaVersion: 1` 保持原有字符串与布尔规则。没有静态文件时继续使用动态 `IPluginSettingsSchema`，仅支持字符串和布尔值，须在获授权且运行后读取。扩展类型和密码保护需要静态版本 2 声明。

整数、枚举与 URL 沿用字符串存储 API，原有方法和枚举数值保持不变。更新时保留同标识的设置和数据；普通字符串键改为秘密字段时，宿主首次打开该插件的设置存储便加密旧值并替换磁盘明文，无法解密的值需要重新填写。发布前按[迁移与兼容性](./migration.md)检查目标宿主版本。
