using WindowsIsland.Contracts;
using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

internal sealed class ExternalPlugin(PluginPaths paths, PluginManifest manifest, string directory)
{
    private SandboxPluginProxy? _instance;
    private readonly object _lifecycle = new();
    private Task _termination = Task.CompletedTask;
    public PluginManifest Manifest { get; } = manifest;
    public string Directory { get; } = directory;
    public string HostRoot => paths.Root;
    public string DataDirectory => paths.Data(Manifest.Id);
    public PluginTrust Trust => PluginTrust.Read(this);
    public IMessageSource Create()
    {
        PluginManifestReader.ValidateForHost(Manifest, ReleaseVersion.Current.Tag);
        if (!PluginConsentPolicy.HasConsent(this)) throw new UnauthorizedAccessException("Plugin consent is required.");
        PluginPaths.CheckTree(Directory);
        lock (_lifecycle)
        {
            if (_instance is not null || !_termination.IsCompleted) throw new InvalidOperationException("The previous plugin worker has not exited.");
            _termination.GetAwaiter().GetResult();
            return _instance = new SandboxPluginProxy(this);
        }
    }

    public void Unload()
    {
        lock (_lifecycle)
        {
            var instance = _instance;
            _instance = null;
            if (instance is null) return;
            _termination = instance.Termination;
            instance.Dispose();
        }
    }

    public Task VerifyUnloadAsync()
    {
        lock (_lifecycle) return _instance is null ? _termination
            : Task.FromException(new InvalidOperationException("The plugin worker is still active."));
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
                        PluginAssemblyValidator.Validate(directory, manifest);
                        if (!ids.Add(id)) throw new PluginException(PluginErrorCode.DuplicateId, "插件标识重复。");
                        result.Add(Registration(manifest, directory));
                    }
                    catch (Exception error) { new PluginDiagnostics(PluginId.IsValid(id) ? id : "wini_plugin_loader", DiagnosticArea.Application).Capture(error, "Discover"); }
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
        PluginConsentProvenance.Require(external);
        var author = manifest.SourceKind == PluginSourceKind.Official && manifest.DeveloperId == PluginCatalogClient.OfficialDeveloperId
            ? PluginCatalogClient.OfficialDeveloperId : manifest.Author;
        var metadata = new PluginMetadata(false, manifest.Version, PluginPaths.Relative(directory, manifest.EntryAssembly), author, external);
        var definition = new NotificationPluginDefinition(id, manifest.Name, "PluginToggle." + id, 0xE8A5,
            DiagnosticArea.Application, _ => store.GetPluginBoolean(id, "Host.Enabled", false),
            (s, enabled) => { store.SetPluginBoolean(id, "Host.Enabled", enabled); return s; }, _ => false, metadata);
        if (PluginDesktopPolicy.IsAllowed(manifest))
            definition = (id == BuiltInPluginIds.Telegram ? BuiltInPluginDefinitions.Telegram : BuiltInPluginDefinitions.WeChat) with
            {
                Metadata = metadata,
                IsEnabled = s => (id == BuiltInPluginIds.Telegram ? s.Telegram : s.WeChat) && store.GetPluginBoolean(id, "Host.Enabled", false),
                SetEnabled = (s, enabled) => { store.SetPluginBoolean(id, "Host.Enabled", enabled); return id == BuiltInPluginIds.Telegram ? s with { Telegram = enabled } : s with { WeChat = enabled }; }
            };
        return new(definition, external.Create);
    }
}

internal sealed class LifecycleSource(IIslandPlugin plugin) : IMessageSource, IPluginStatusProvider, IPluginSettingsSchema, IPluginLifecycleAware
{
    public bool HasLifecycleHooks => plugin is IPluginLifecycleAware;
    public IReadOnlyList<PluginSetting> SettingsSchema => (plugin as IPluginSettingsSchema)?.SettingsSchema ?? [];
    public Task OnStoppingAsync(PluginStopReason reason) => (plugin as IPluginLifecycleAware)?.OnStoppingAsync(reason) ?? Task.CompletedTask;
    public Task OnUninstallAsync() => (plugin as IPluginLifecycleAware)?.OnUninstallAsync() ?? Task.CompletedTask;
    public string Id => plugin.Id;
    public string DisplayName => plugin.DisplayName;
    public PluginStatus Status => (plugin as IPluginStatusProvider)?.Status ?? new(PluginConnectionState.Connected);
    public event Action<PluginNotification>? NotificationReceived { add { } remove { } }
    public Task InitializeAsync(IPluginContext context) => plugin.InitializeAsync(context);
    public Task StartAsync() => plugin.StartAsync();
    public Task StopAsync() => plugin.StopAsync();
    public void Dispose() => plugin.Dispose();
}
