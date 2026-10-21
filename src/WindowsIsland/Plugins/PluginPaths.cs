using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

internal sealed class PluginPaths
{
    public string Root { get; }
    public PluginPaths(string? root = null) => Root = Path.GetFullPath(root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsIsland", "Plugins"));
    public string Slot(PluginSourceKind kind) => Path.Combine(Root, kind == PluginSourceKind.Official ? "official" : "community");
    public string Code(PluginSourceKind kind, string id) => Path.Combine(Slot(kind), ValidId(id));
    public string Data(string id) => Path.Combine(Root, "data", ValidId(id));
    private static string ValidId(string id) => PluginId.IsValid(id) && id.Length <= 80 ? id : throw new InvalidDataException("插件标识无效。");

    public static string Relative(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Any(c => c < 32 || c is ':' or '\\' or '<' or '>' or '"' or '|' or '?' or '*')
            || Path.IsPathRooted(relative) || relative.Split('/').Any(p => p is ".." or "." or "" || p.EndsWith('.') || p.EndsWith(' ')))
            throw new InvalidDataException("插件路径无效。");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("插件路径超出目录。");
        CheckLinks(path);
        return path;
    }

    public static void CheckLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("插件目录不能包含符号链接或重解析点。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static void CheckTree(string root)
    {
        CheckLinks(root);
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            CheckLinks(path);
            if (Directory.Exists(path)) CheckTree(path);
        }
    }

    public static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        CheckTree(path);
        Directory.Delete(path, recursive: true);
    }
}
