using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;

namespace WindowsIsland.Plugin.Cli;

internal static class PackageProject
{
    public static PluginManifest ValidateSource(string root)
    {
        var path = PluginPaths.Relative(root, "manifest.json");
        using var input = PluginFileSafety.OpenSingleLinkRead(path);
        if (input.Length > 256 * 1024) throw new InvalidDataException("manifest.json: 清单不能超过 256 KiB。");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(input, PluginManifestReader.Json)
            ?? throw new InvalidDataException("manifest.json: 需要 JSON 对象。");
        PluginManifestReader.Validate(manifest);
        _ = PluginPaths.Relative(root, manifest.EntryAssembly);
        if (manifest.Permissions.Any(p => !PluginPermissions.IsKnown(p)))
            throw new InvalidDataException("manifest.json $.permissions: 存在未知权限；例如 [\"notifications\"]。");
        if (manifest.Permissions.Contains(PluginPermissions.Network) && manifest.Network is null)
            throw new InvalidDataException("manifest.json $.network: network 权限必须提供 origins、purpose 和 dataCategories。");
        _ = PluginStaticSettings.Read(root);
        return manifest;
    }

    public static void ValidatePackage(string root)
    {
        PluginPaths.CheckTree(root);
        var manifest = ValidateSource(root);
        PluginManifestReader.Read(root);
        PluginAssemblyValidator.Validate(root, manifest);
        long total = 0;
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (++count > 4096) throw new InvalidDataException("package: 文件数超过 4096。");
            _ = PluginPaths.Relative(root, Path.GetRelativePath(root, path).Replace('\\', '/'));
            using var input = PluginFileSafety.OpenSingleLinkRead(path);
            total = checked(total + input.Length);
            if (total > 256L * 1024 * 1024) throw new InvalidDataException("package: 解压大小超过 256 MiB。");
            if (Path.GetFileName(path).Equals("WindowsIsland.Contracts.dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("package: 移除 WindowsIsland.Contracts.dll，由宿主提供合约。");
        }
    }

    public static async Task<string> Build(string root)
    {
        var manifest = ValidateSource(root);
        var projects = Directory.GetFiles(root, "*.csproj");
        if (projects.Length != 1) throw new InvalidDataException("project: 根目录需要且只能有一个 .csproj。");
        // 每次生成新的工作目录，避免旧版本依赖残留进入 ZIP，也不递归删除用户目录。
        var output = PluginPaths.Relative(root, ".wini/build/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        await Dotnet(root, "build", projects[0], "--configuration", "Release", "--output", output, "--disable-build-servers");
        var stage = PluginPaths.Relative(root, ".wini/packages/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        PluginPaths.CheckTree(output);
        foreach (var source in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(output, source).Replace('\\', '/');
            if (Path.GetFileName(source).StartsWith("WindowsIsland.Contracts.", StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.GetExtension(source) is ".pdb" or ".xml") continue;
            var target = PluginPaths.Relative(stage, name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = PluginFileSafety.OpenSingleLinkRead(source);
            using var destination = new FileStream(target, FileMode.CreateNew);
            input.CopyTo(destination);
        }
        foreach (var name in new[] { "manifest.json", PluginStaticSettings.FileName })
        {
            var source = PluginPaths.Relative(root, name);
            if (!File.Exists(source)) continue;
            File.Copy(source, PluginPaths.Relative(stage, name), overwrite: true);
        }
        ValidatePackage(stage);
        Console.WriteLine($"{manifest.Id}: {stage}");
        return stage;
    }

    public static async Task Pack(string root)
    {
        var stage = await Build(root);
        var manifest = ValidateSource(stage);
        var dist = PluginPaths.Relative(root, "dist");
        Directory.CreateDirectory(dist);
        var zip = PluginPaths.Relative(dist, manifest.Id + "-" + manifest.Version + ".zip");
        // 不覆盖既有发布包。
        using (var output = new FileStream(zip, FileMode.CreateNew, FileAccess.Write))
        {
            using var archive = new ZipArchive(output, ZipArchiveMode.Create);
            foreach (var path in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(Path.GetRelativePath(stage, path).Replace('\\', '/'), CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var source = PluginFileSafety.OpenSingleLinkRead(path);
                using var target = entry.Open();
                source.CopyTo(target);
            }
        }
        using var content = File.OpenRead(zip);
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        using var digest = new StreamWriter(new FileStream(zip + ".sha256", FileMode.CreateNew));
        digest.WriteLine(hash);
        Console.WriteLine(zip);
        Console.WriteLine(hash);
    }

    internal static async Task Dotnet(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 dotnet。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        if (process.ExitCode != 0) throw new IOException($"dotnet 退出码 {process.ExitCode}。");
    }
}
