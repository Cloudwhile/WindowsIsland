using System.Reflection;
using System.Runtime.Loader;
using WindowsIsland.Contracts;

namespace WindowsIsland.PluginWorker;

internal sealed class WorkerLoadContext : AssemblyLoadContext
{
    private readonly string _root;
    private readonly AssemblyDependencyResolver _resolver;

    public WorkerLoadContext(string root, string entryAssembly) : base(isCollectible: true)
    {
        _root = Path.GetFullPath(root);
        _resolver = new AssemblyDependencyResolver(ResolveRelative(entryAssembly));
    }

    public IIslandPlugin Create(PluginManifest manifest)
    {
        var assembly = LoadFromAssemblyPath(ResolveRelative(manifest.EntryAssembly));
        var type = assembly.GetType(manifest.EntryType, throwOnError: true)!;
        if (type.IsAbstract || !type.IsPublic || !typeof(IIslandPlugin).IsAssignableFrom(type))
            throw new InvalidDataException("Invalid plugin entry.");
        var plugin = Activator.CreateInstance(type) as IIslandPlugin
            ?? throw new InvalidDataException("Invalid plugin entry.");
        if (plugin.Id != manifest.Id) throw new InvalidDataException("Invalid plugin identity.");
        return plugin;
    }

    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name == typeof(IIslandPlugin).Assembly.GetName().Name) return typeof(IIslandPlugin).Assembly;
        if (name.Name is null || name.Name.Equals("WindowsIsland", StringComparison.OrdinalIgnoreCase)
            || name.Name.StartsWith("WindowsIsland.PluginWorker", StringComparison.OrdinalIgnoreCase)
            || name.Name.StartsWith("WindowsIsland.PluginIsolation", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Host assembly references are unavailable.");
        var path = _resolver.ResolveAssemblyToPath(name);
        if (path is null && name.Name.IndexOfAny(['/', '\\', ':']) < 0)
        {
            var candidate = ResolveRelative(name.Name + ".dll");
            if (File.Exists(candidate)) path = candidate;
        }
        return path is null ? null : LoadFromAssemblyPath(WithinRoot(path));
    }

    protected override nint LoadUnmanagedDll(string name)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(name);
        return path is null ? 0 : LoadUnmanagedDllFromPath(WithinRoot(path));
    }

    private string ResolveRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
            || relative.Any(c => char.IsControl(c) || c is ':' or '\\' or '<' or '>' or '"' or '|' or '?' or '*')
            || relative.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ')))
            throw new InvalidDataException("Invalid plugin dependency path.");
        return WithinRoot(Path.Combine(_root, relative));
    }

    private string WithinRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid plugin dependency path.");
        for (var current = fullPath; current is not null; current = Path.GetDirectoryName(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Invalid plugin dependency path.");
            if (current.Equals(_root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return fullPath;
    }
}
