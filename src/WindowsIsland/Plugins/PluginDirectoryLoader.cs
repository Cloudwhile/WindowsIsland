using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using WindowsIsland.Contracts;
using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

internal sealed class ExternalPlugin(PluginPaths paths, PluginManifest manifest, string directory)
{
    private PluginLoadContext? _context;
    public PluginManifest Manifest { get; } = manifest;
    public string Directory { get; } = directory;
    public string DataDirectory => paths.Data(Manifest.Id);
    public PluginTrust Trust => PluginTrust.Read(this);
    public WeakReference? LastUnload { get; private set; }
    public IMessageSource Create()
    {
        PluginPaths.CheckTree(Directory);
        System.IO.Directory.CreateDirectory(DataDirectory);
        PluginPaths.CheckLinks(DataDirectory);
        var context = _context = new PluginLoadContext(PluginPaths.Relative(Directory, Manifest.EntryAssembly), Directory);
        try
        {
            var type = context.LoadFromAssemblyPath(PluginPaths.Relative(Directory, Manifest.EntryAssembly)).GetType(Manifest.EntryType, throwOnError: true)!;
            if (type.IsAbstract || !typeof(IIslandPlugin).IsAssignableFrom(type)) throw new InvalidDataException("入口必须实现 IIslandPlugin。");
            var plugin = (IIslandPlugin)Activator.CreateInstance(type)!;
            return plugin as IMessageSource ?? new LifecycleSource(plugin);
        }
        catch { Unload(); throw; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Unload()
    {
        var context = Interlocked.Exchange(ref _context, null);
        if (context is null) return;
        LastUnload = new WeakReference(context);
        context.Unload();
    }

    public async Task VerifyUnloadAsync()
    {
        var weak = LastUnload;
        if (weak is null) return;
        await Task.Run(() =>
        {
            for (var i = 0; i < 5 && weak.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            if (weak.IsAlive) new PluginDiagnostics(Manifest.Id, DiagnosticArea.Application).Capture(
                new InvalidOperationException("Plugin context is still referenced."), "Unload");
        });
    }
}

internal sealed class PluginLoadContext(string assembly, string directory) : AssemblyLoadContext(isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(assembly);
    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name == typeof(IIslandPlugin).Assembly.GetName().Name) return typeof(IIslandPlugin).Assembly;
        if (name.Name == "WindowsIsland") throw new FileLoadException("插件不能引用宿主程序集。");
        var path = _resolver.ResolveAssemblyToPath(name);
        if (path is null && name.Name is { } simple && simple.IndexOfAny(['/', '\\', ':']) < 0)
        {
            var local = PluginPaths.Relative(directory, simple + ".dll");
            if (File.Exists(local)) path = local;
        }
        if (path is null) return null;
        var safe = PluginPaths.Relative(directory, Path.GetRelativePath(directory, path).Replace('\\', '/'));
        return LoadFromAssemblyPath(safe);
    }
    protected override nint LoadUnmanagedDll(string name)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(name);
        return path is null ? 0 : LoadUnmanagedDllFromPath(PluginPaths.Relative(directory, Path.GetRelativePath(directory, path).Replace('\\', '/')));
    }
}

internal sealed class PluginDirectoryLoader(PluginPaths paths, SettingsStore store)
{
    public IReadOnlyList<NotificationPluginRegistration> Scan()
    {
        var result = new List<NotificationPluginRegistration>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in new[] { PluginSourceKind.Official, PluginSourceKind.Manual })
        {
            try
            {
                var slot = paths.Slot(kind);
                PluginPaths.CheckLinks(slot);
                if (!Directory.Exists(slot)) continue;
                foreach (var directory in Directory.EnumerateDirectories(slot))
                {
                    var id = Path.GetFileName(directory);
                    if (id.EndsWith(".bak", StringComparison.Ordinal) || id.StartsWith('.')) continue;
                    try
                    {
                        PluginPaths.CheckTree(directory);
                        var manifest = PluginManifestReader.Read(directory, id) with { SourceKind = kind };
                        if (!ids.Add(id)) throw new InvalidDataException("插件标识重复。");
                        result.Add(Registration(manifest, directory));
                    }
                    catch (Exception error) { new PluginDiagnostics(PluginId.IsValid(id) ? id : "wini_loader_plugin", DiagnosticArea.Application).Capture(error, "Discover"); }
                }
            }
            catch (Exception error) { Diagnostics.Capture(DiagnosticArea.Application, error, "ScanPlugins"); }
        }
        return result;
    }

    public NotificationPluginRegistration Registration(PluginManifest manifest, string directory)
    {
        var id = manifest.Id;
        var external = new ExternalPlugin(paths, manifest, directory);
        var author = manifest.SourceKind == PluginSourceKind.Official && manifest.DeveloperId == PluginCatalogClient.OfficialDeveloperId
            ? "WINI" : manifest.Author;
        var metadata = new PluginMetadata(false, manifest.Version, PluginPaths.Relative(directory, manifest.EntryAssembly), author, external);
        var definition = new NotificationPluginDefinition(id, manifest.Name, "PluginToggle." + id, 0xE8A5,
            DiagnosticArea.Application, _ => store.GetPluginBoolean(id, "Host.Enabled", true),
            (s, enabled) => { store.SetPluginBoolean(id, "Host.Enabled", enabled); return s; }, _ => false, metadata);
        return new(definition, external.Create);
    }
}

internal sealed class LifecycleSource(IIslandPlugin plugin) : IMessageSource, IPluginStatusProvider
{
    public string Id => plugin.Id;
    public string DisplayName => plugin.DisplayName;
    public PluginStatus Status => (plugin as IPluginStatusProvider)?.Status ?? new(PluginConnectionState.Connected);
    public event Action<PluginNotification>? NotificationReceived { add { } remove { } }
    public Task InitializeAsync(IPluginContext context) => plugin.InitializeAsync(context);
    public Task StartAsync() => plugin.StartAsync();
    public Task StopAsync() => plugin.StopAsync();
    public void Dispose() => plugin.Dispose();
}
