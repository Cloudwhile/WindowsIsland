namespace WindowsIsland.Plugins;

// 宿主专属记录不接受旧插件目录的路径、重解析点或硬链接。
internal static class PluginHostFiles
{
    public static string PathFor(string root, string area, string id)
    {
        var directory = PluginPaths.Relative(root, area);
        if (OperatingSystem.IsWindows()) SandboxAccess.ProtectHostDirectory(directory);
        var path = PluginPaths.Relative(directory, id + ".json");
        if (File.Exists(path)) PluginFileSafety.CheckSingleLink(path);
        return path;
    }

    public static void WriteAtomic(string path, string content)
    {
        PluginPaths.CheckLinks(path);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsWindows()) SandboxAccess.ProtectHostDirectory(directory);
        var temporary = Path.Combine(directory, ".consent-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false, true))) writer.Write(content);
            PluginPaths.CheckLinks(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
