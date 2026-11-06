using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Xml.Linq;
using Windows.Management.Deployment;
using WindowsIsland.Services;

internal static class Program
{
    private const string Publisher = "CN=WindowsIslandVerification";
    private const string DirectoryPrefix = "WindowsIsland.InitializationVerification-";
    private const string MetadataDirectory = "microsoft.system.package.metadata";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(60);

    private static async Task<int> Main(string[] arguments)
    {
        try
        {
            if (arguments.Length == 2 && arguments[0] == "--initialization-test-helper")
                return await RunHelperAsync(arguments[1]);
            if (arguments.Length != 1 || arguments[0] != "--allow-uac")
            {
                Console.WriteLine("使用普通权限运行：dotnet run --project tests/WindowsIsland.InitializationTests -c Release -- --allow-uac");
                Console.WriteLine("此检查会请求一次 UAC，在 Program Files 创建并清理独立测试包。请先生成应用发布目录。");
                return 2;
            }
            Require(!IsAdministrator(), "测试主体必须使用普通权限，不能在管理员终端中运行。");
            await RunChecksAsync();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"FAIL {error.GetType().Name} 0x{error.HResult:X8}");
            return 1;
        }
    }

    private static async Task RunChecksAsync()
    {
        var workspace = Workspace();
        var id = Guid.NewGuid().ToString("N");
        var output = Path.Combine(workspace, "artifacts", "verification", "initialization", id);
        var directory = ProtectedDirectory(id);
        Directory.CreateDirectory(output);
        var packageName = "Island.Init." + id[..20];
        PrepareFixture(workspace, output, packageName);
        using var parent = Process.GetCurrentProcess();
        var job = new Job(parent.Id, parent.StartTime.ToUniversalTime().Ticks,
            WindowsIdentity.GetCurrent().User!.Value, packageName);
        var jobPath = Path.Combine(output, "job.json");
        File.WriteAllText(jobPath, JsonSerializer.Serialize(job));
        var manager = new PackageManager();
        var original = NormalRegistration(manager);
        Process? helper = null;
        try
        {
            var request = new ProcessStartInfo(Environment.ProcessPath!)
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            request.ArgumentList.Add("--initialization-test-helper");
            request.ArgumentList.Add(jobPath);
            helper = await Task.Run(() => Process.Start(request)).WaitAsync(WaitLimit)
                ?? throw new IOException("测试辅助进程未启动。");
            await WaitAsync(output, "ready", helper);
            Require(!Directory.Exists(Path.Combine(directory, MetadataDirectory)), "首次检查前不能已有元数据目录。");
            Require(ReadOnly(directory), "测试目录必须对普通用户只读。");
            var refused = false;
            try { await RegisterAsync(manager, directory); }
            catch (UnauthorizedAccessException error) when (error.HResult == unchecked((int)0x80070005)) { refused = true; }
            Require(refused, "缺少元数据目录时必须复现原始访问拒绝。");
            Console.WriteLine("PASS protected first registration reproduces 0x80070005");

            var requests = 0;
            var elevated = await InitializationPreparation.PrepareAsync(directory, null, async _ =>
            {
                requests++;
                Publish(output, "prepare");
                await WaitAsync(output, "prepared", helper);
            });
            Require(elevated && requests == 1, "实际初始化准备必须仅请求一次辅助操作。");
            Require(Directory.Exists(Path.Combine(directory, MetadataDirectory)), "实际初始化准备必须创建元数据目录。");
            Require(ReadOnly(directory) && ReadOnly(Path.Combine(directory, MetadataDirectory)), "初始化不能授予普通用户写入安装或元数据目录的权限。");
            Console.WriteLine("PASS actual initialization preparation preserves protected directory permissions");

            await RegisterAsync(manager, directory);
            var registered = manager.FindPackagesForUser("").Single(package => package.Id.Name == packageName);
            Require(string.Equals(Path.TrimEndingDirectorySeparator(registered.InstalledLocation.Path), directory,
                StringComparison.OrdinalIgnoreCase), "独立包必须注册到本次测试目录。");
            Console.WriteLine("PASS requesting user registers the prepared package without elevation");
        }
        finally
        {
            Publish(output, "done");
            if (helper is not null)
            {
                await helper.WaitForExitAsync().WaitAsync(WaitLimit);
                var exitCode = helper.ExitCode;
                helper.Dispose();
                Require(exitCode == 0, "测试辅助进程清理失败。");
            }
            Require(!manager.FindPackagesForUser("").Any(package => package.Id.Name == packageName), "测试包注册必须被清理。");
            Require(!Directory.Exists(directory), "Program Files 测试目录必须被清理。");
            Require(original.SequenceEqual(NormalRegistration(manager)), "正常应用的包注册不能改变。");
            Console.WriteLine("PASS isolated package and directory removed; normal application registration preserved");
        }
    }

    private static void PrepareFixture(string workspace, string output, string packageName)
    {
        var payload = Path.Combine(output, "payload");
        var publish = Path.Combine(workspace, "artifacts", "publish");
        var initialization = Path.Combine(payload, "Initialization");
        Directory.CreateDirectory(initialization);
        foreach (var relative in new[] { "WindowsIsland.exe", "WindowsIsland.pri", "Assets/Shell/StoreLogo.png",
            "Assets/Shell/MediumTile.png", "Assets/Shell/AppList.png" })
        {
            var destination = Path.Combine(payload, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(publish, relative), destination);
        }
        var template = XDocument.Load(Path.Combine(workspace, "packaging", "AppxManifest.xml"));
        var identity = template.Root!.Element(template.Root.Name.Namespace + "Identity")!;
        identity.SetAttributeValue("Name", packageName);
        identity.SetAttributeValue("Publisher", Publisher);
        template.Save(Path.Combine(initialization, "AppxManifest.xml"));
        new XDocument(new XElement(template.Root.Name, new XElement(template.Root.Name.Namespace + "Extensions")))
            .Save(Path.Combine(initialization, "RuntimeManifest.xml"));
        InitializationPreparation.Prepare(payload, null);
    }

    private static async Task<int> RunHelperAsync(string jobPath)
    {
        var output = Path.GetDirectoryName(Path.GetFullPath(jobPath))!;
        var id = Path.GetFileName(output);
        var expectedOutput = Path.Combine(Workspace(), "artifacts", "verification", "initialization", id);
        Require(Guid.TryParseExact(id, "N", out _) && SamePath(output, expectedOutput)
            && SamePath(jobPath, Path.Combine(expectedOutput, "job.json")) && IsAdministrator(), "无效测试辅助请求。");
        var job = JsonSerializer.Deserialize<Job>(File.ReadAllText(jobPath)) ?? throw new InvalidDataException("缺少测试请求。");
        Require(job.PackageName == "Island.Init." + id[..20], "测试包身份必须与目录一致。");
        using var parent = Process.GetProcessById(job.ParentId);
        Require(parent.StartTime.ToUniversalTime().Ticks == job.ParentStarted
            && SamePath(parent.MainModule!.FileName!, Environment.ProcessPath!), "测试发起进程不匹配。");
        _ = new SecurityIdentifier(job.UserSid);
        var target = ProtectedDirectory(id);
        Require(!Directory.Exists(target), "测试目录必须尚未存在。");
        var created = false;
        try
        {
            var payload = Path.Combine(output, "payload");
            RejectLinks(payload);
            Directory.CreateDirectory(target);
            created = true;
            foreach (var file in Directory.GetFiles(payload, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(target, Path.GetRelativePath(payload, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            await SetSystemOwnerAsync(target);
            Publish(output, "ready");
            await WaitForParentAsync(output, "prepare", parent);
            if (!File.Exists(Path.Combine(output, "done")))
            {
                InitializationPreparation.Prepare(target, null);
                Publish(output, "prepared");
                await WaitForParentAsync(output, "done", parent);
            }
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "helper-error"), $"{error.GetType().Name} 0x{error.HResult:X8}");
            return 1;
        }
        finally
        {
            if (created)
            {
                var manager = new PackageManager();
                foreach (var package in manager.FindPackages(job.PackageName, Publisher).ToArray())
                    await manager.RemovePackageAsync(package.Id.FullName, RemovalOptions.RemoveForAllUsers).AsTask().WaitAsync(WaitLimit);
                Require(SamePath(target, ProtectedDirectory(id)), "清理路径超出本次测试范围。");
                if (Directory.Exists(target))
                {
                    RejectLinks(target);
                    Directory.Delete(target, recursive: true);
                }
            }
        }
    }

    private static async Task RegisterAsync(PackageManager manager, string directory)
    {
        var operation = manager.RegisterPackageAsync(new Uri(Path.Combine(directory, "AppxManifest.xml")), null,
            DeploymentOptions.DevelopmentMode);
        try
        {
            var result = await operation.AsTask().WaitAsync(WaitLimit);
            if (result.ExtendedErrorCode is not null) throw result.ExtendedErrorCode;
        }
        catch (TimeoutException) { operation.Cancel(); throw; }
    }

    private static async Task WaitAsync(string output, string marker, Process helper)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(output, marker)))
        {
            Require(!helper.HasExited && !File.Exists(Path.Combine(output, "helper-error")), "测试辅助操作失败。");
            if (timer.Elapsed > WaitLimit) throw new TimeoutException("测试辅助操作超时。");
            await Task.Delay(50);
        }
    }

    private static async Task WaitForParentAsync(string output, string marker, Process parent)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(output, marker)) && !File.Exists(Path.Combine(output, "done")) && !parent.HasExited)
        {
            if (timer.Elapsed > WaitLimit) throw new TimeoutException("测试发起进程超时。");
            await Task.Delay(50);
        }
    }

    private static async Task SetSystemOwnerAsync(string directory)
    {
        var request = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "icacls.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { directory, "/setowner", "*S-1-5-18", "/q" }) request.ArgumentList.Add(argument);
        using var process = Process.Start(request)!;
        await process.WaitForExitAsync().WaitAsync(WaitLimit);
        Require(process.ExitCode == 0, "无法设置隔离夹具的受保护所有者。");
    }

    private static bool ReadOnly(string directory)
    {
        var probe = Path.Combine(directory, ".initialization-verification-probe");
        try { using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write); }
        catch (UnauthorizedAccessException) { return true; }
        File.Delete(probe);
        return false;
    }

    private static void RejectLinks(string directory)
    {
        Require((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0, "测试目录不能是链接。");
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            Require((attributes & FileAttributes.ReparsePoint) == 0, "测试载荷不能包含链接。");
            if ((attributes & FileAttributes.Directory) != 0) RejectLinks(entry);
        }
    }

    private static string Workspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "packaging", "AppxManifest.xml"))) return directory.FullName;
        throw new DirectoryNotFoundException("无法定位仓库。");
    }

    private static string[] NormalRegistration(PackageManager manager) => manager.FindPackagesForUser("")
        .Where(package => package.Id.Name == "WindowsIsland.Desktop")
        .Select(package => package.Id.FullName + "|" + package.InstalledLocation.Path).Order().ToArray();

    private static string ProtectedDirectory(string id) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), DirectoryPrefix + id);

    private static bool IsAdministrator() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static void Publish(string directory, string marker)
    {
        var path = Path.Combine(directory, marker);
        File.WriteAllText(path + ".tmp", "ready");
        File.Move(path + ".tmp", path, overwrite: true);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed record Job(int ParentId, long ParentStarted, string UserSid, string PackageName);
}
