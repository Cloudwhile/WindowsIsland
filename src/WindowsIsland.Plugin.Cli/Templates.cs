using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;

namespace WindowsIsland.Plugin.Cli;

internal static partial class Templates
{
    public static void Create(string name, string template)
    {
        if (!ProjectName().IsMatch(name)) throw new ArgumentException("Name: 使用 1–48 个英文字母或数字，并以字母开头，例如 HelloIsland。");
        if (template is not ("notification" or "pinned" or "network")) throw new ArgumentException("template: notification、pinned 或 network。");
        var root = PluginPaths.Relative(Environment.CurrentDirectory, name);
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("目标已存在，请使用新的项目名称。");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "sdk"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.Copy(typeof(IIslandPlugin).Assembly.Location, Path.Combine(root, "sdk", "WindowsIsland.Contracts.dll"));
        var id = "wini_plugin_" + name.ToLowerInvariant();
        var network = template == "network" ? new PluginNetworkDisclosure(["https://example.com"], "读取示例状态", ["请求路径"]) : null;
        var manifest = new PluginManifest(id, name, "1.0.0", name + ".dll", name + ".Plugin", 1,
            template == "network" ? ["notifications", "network"] : ["notifications"], Author: "Plugin author", Network: network);
        Write("manifest.json", JsonSerializer.Serialize(manifest, PluginManifestReader.Json));
        Write(name + ".csproj", Project.Replace("NAME", name));
        Write("src/Plugin.cs", Source.Replace("NAMESPACE", name).Replace("PLUGIN_ID", id)
            .Replace("BODY", Body(template).Replace("\n", "\n            ")));
        Write("plugin.test.json", TestPlan(template));
        Write("settings.schema.json", """
            {"schemaVersion":1,"fields":[{"key":"enabled","type":"boolean","title":"运行示例","default":true}]}
            """);
        Write(".gitignore", "bin/\nobj/\n.wini/\ndist/\n");
        Write("README.md", $"# {name}\n\n需要 Windows 和 .NET 10 SDK。合约 ABI 为 1.0.0.0，插件合约版本为 1。\n\n```powershell\nwini-plugin validate\nwini-plugin test\nwini-plugin pack\n```\n\nplugin.test.json 包含正常运行及立即停止两个场景；测试使用独立 Worker 和数据，网络响应为固定模拟值。结果写入 .wini/tests，不显示正式应用界面。\n\n从 dist 导入 ZIP，在插件管理中启用并接受披露。停用会取消任务并撤下固定内容。\n\n网络模板默认不会联网；先按实际服务修改清单和代码中的目的地及用途，再将 send 设置为 true。\n");
        if (template == "network") Write("settings.schema.json", """
            {"schemaVersion":1,"fields":[{"key":"enabled","type":"boolean","title":"运行示例","default":true},{"key":"send","type":"boolean","title":"发送示例请求","default":false}]}
            """);
        Console.WriteLine(root);
        void Write(string relative, string text) => File.WriteAllText(Path.Combine(root, relative), text);
    }

    private static string Body(string template) => template switch
    {
        "notification" => """
            context.PublishNotification(new PluginNotification(Id, "示例通知", null, "你好，消息岛", DateTimeOffset.UtcNow,
                DeduplicationKey: Guid.NewGuid().ToString("N"), AppName: DisplayName));
            """,
        "pinned" => """
            var pinned = context.PinnedContent;
            if (pinned is null) return;
            var count = 0;
            var enabled = true;
            var step = 1;
            PinnedContent Content() => new("counter", Id, "计数器", count.ToString(), Controls:
                [new PinnedControl("add", "增加", PinnedControlKind.Button, IsEnabled: enabled, Symbol: "Add"),
                 new PinnedControl("enabled", "允许增加", PinnedControlKind.Toggle, IsOn: enabled),
                 new PinnedControl("step", "每次增加", PinnedControlKind.Slider, Minimum: 1, Maximum: 10, Value: step, Step: 1)]);
            pinned.SetInteractionHandler(interaction =>
            {
                if (token.IsCancellationRequested || interaction.ContentId != "counter") return Task.CompletedTask;
                switch (interaction.ControlId)
                {
                    case "add" when enabled: count += step; break;
                    case "enabled" when interaction.IsOn is { } value: enabled = value; break;
                    case "step" when interaction.Value is >= 1 and <= 10: step = (int)interaction.Value.Value; break;
                    default: return Task.CompletedTask;
                }
                pinned.Update(Content());
                return Task.CompletedTask;
            });
            pinned.Submit(Content());
            """,
        _ => """
            if (!context.Settings.GetBoolean("send", false)) return;
            var network = context.GetService<IPluginNetwork>();
            if (network is null) return;
            var response = await network.SendAsync(new("https://example.com/", "GET", "读取示例状态"), token);
            token.ThrowIfCancellationRequested();
            context.PublishNotification(new PluginNotification(Id, "示例状态", null, response.StatusCode.ToString(), DateTimeOffset.UtcNow));
            """
    };

    private static string TestPlan(string template) => template switch
    {
        "notification" => """
            {"schemaVersion":1,"scenarios":[
              {"minimumNotifications":1,"maximumNotifications":1},
              {"waitMilliseconds":0,"settleMilliseconds":1200}
            ]}
            """,
        "pinned" => """
            {"schemaVersion":1,"scenarios":[
              {"pinnedInteractions":[
                {"contentId":"counter","controlId":"step","value":3},
                {"contentId":"counter","controlId":"add"},
                {"contentId":"counter","controlId":"enabled","isOn":false},
                {"contentId":"counter","controlId":"enabled","isOn":true},
                {"contentId":"counter","controlId":"add"}
              ],"expectedCompactText":"6"},
              {"waitMilliseconds":0,"settleMilliseconds":1200}
            ]}
            """,
        _ => """
            {"schemaVersion":1,"scenarios":[
              {"settings":{"send":true},"stubNetwork":true,"networkRequests":1,
               "minimumNotifications":1,"maximumNotifications":1},
              {"settings":{"send":true},"networkRequests":1,"deniedNetworkRequests":1,"diagnostics":1},
              {"settings":{"send":true},"stubNetwork":true,"waitMilliseconds":0,"settleMilliseconds":1200}
            ]}
            """
    };

    private const string Project = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
            <AssemblyName>NAME</AssemblyName>
            <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
            <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
          </PropertyGroup>
          <ItemGroup>
            <Compile Include="src/**/*.cs" />
            <Reference Include="WindowsIsland.Contracts"><HintPath>sdk/WindowsIsland.Contracts.dll</HintPath><Private>false</Private></Reference>
          </ItemGroup>
        </Project>
        """;
    private const string Source = """
        using WindowsIsland.Contracts;
        namespace NAMESPACE;

        public sealed class Plugin : IIslandPlugin
        {
            private IPluginContext? _context;
            private CancellationTokenSource? _shutdown;
            private Task? _run;
            public string Id => "PLUGIN_ID";
            public string DisplayName => "NAMESPACE";
            public Task InitializeAsync(IPluginContext context) { _context = context; return Task.CompletedTask; }
            public Task StartAsync()
            {
                if (_run is not null) return Task.CompletedTask;
                var context = _context ?? throw new InvalidOperationException("尚未初始化");
                _shutdown = new CancellationTokenSource();
                _run = RunAsync(context, _shutdown.Token);
                return Task.CompletedTask;
            }
            private async Task RunAsync(IPluginContext context, CancellationToken token)
            {
                try
                {
                    await Task.Delay(1000, token);
                    if (!context.Settings.GetBoolean("enabled", true)) return;
                    token.ThrowIfCancellationRequested();
                    BODY
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception error)
                {
                    context.Diagnostics.Write(new PluginDiagnostic("Plugin", "Run", AdditionalInformation:
                        new PluginDiagnosticInfo(error.GetType().FullName, error.HResult)));
                }
            }
            public async Task StopAsync()
            {
                _shutdown?.Cancel();
                try { if (_run is not null) await _run; }
                finally
                {
                    _context?.PinnedContent?.SetInteractionHandler(null);
                    _context?.PinnedContent?.Remove("counter");
                    _shutdown?.Dispose(); _shutdown = null; _run = null;
                }
            }
            public void Dispose()
            {
                // 生命周期要求先 StopAsync；直接释放也取消尚未完成的任务。
                _shutdown?.Cancel(); _shutdown?.Dispose(); _shutdown = null;
                _context?.PinnedContent?.SetInteractionHandler(null);
                _context?.PinnedContent?.Remove("counter");
                _context = null;
            }
        }
        """;

    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9]{0,47}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectName();
}
