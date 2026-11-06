using WindowsIsland.Plugin.Cli;
using WindowsIsland.Plugins;

try
{
    if (args.Length == 0 || args[0] is "--help" or "help")
    {
        Console.WriteLine("wini-plugin init <Name> [--template notification|pinned|network]\nwini-plugin validate [directory]\nwini-plugin doctor [directory] [--host-version 0.1.0]\nwini-plugin build [directory]\nwini-plugin pack [directory]\nwini-plugin test [directory]");
        return 0;
    }
    if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("请在 Windows 上使用插件工具。");
    if (args[0] == "init")
    {
        if (args.Length != 2 && !(args.Length == 4 && args[2] == "--template")) throw new ArgumentException("用法：wini-plugin init HelloIsland --template notification");
        Templates.Create(args[1], args.Length == 4 ? args[3] : "notification");
        return 0;
    }
    string? hostVersion = null;
    var arguments = args.Skip(1).ToList();
    if (args[0] == "doctor" && arguments.IndexOf("--host-version") is var option && option >= 0)
    {
        if (option + 1 >= arguments.Count) throw new ArgumentException("--host-version 需要 SemVer 参数，例如 0.1.0。");
        hostVersion = arguments[option + 1];
        arguments.RemoveRange(option, 2);
    }
    if (arguments.Count > 1 || arguments.Any(value => value.StartsWith("--", StringComparison.Ordinal)))
        throw new ArgumentException("仅接受一个项目目录参数；doctor 可添加 --host-version 0.1.0。");
    var root = Path.GetFullPath(arguments.Count == 1 ? arguments[0] : ".");
    switch (args[0])
    {
        case "validate": PackageProject.ValidateSource(root); break;
        case "doctor":
            await PackageProject.Dotnet(root, "--version");
            PackageProject.ValidatePackage(root);
            if (hostVersion is not null) PluginManifestReader.ValidateForHost(PackageProject.ValidateSource(root), hostVersion);
            else Console.WriteLine("未指定目标宿主版本；使用 --host-version 0.1.0 检查最低版本兼容性。");
            break;
        case "build": await PackageProject.Build(root); break;
        case "pack": await PackageProject.Pack(root); break;
        case "test": await PluginTest.Run(root); break;
        default: throw new ArgumentException("未知命令；运行 wini-plugin --help 查看命令。");
    }
    Console.WriteLine("检查完成。");
    return 0;
}
catch (PluginException error)
{
    Console.Error.WriteLine(CliDiagnostic.Format(error));
    return 1;
}
catch (System.Text.Json.JsonException error)
{
    Console.Error.WriteLine($"JSON {error.Path ?? "$"}, line {error.LineNumber + 1}, byte {error.BytePositionInLine}: 无效 JSON；字段名与字符串使用双引号，例如 {{\"schemaVersion\":1}}。");
    return 1;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
