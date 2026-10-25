using WindowsIsland.Contracts;
using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

internal sealed record PluginSettingsModel(IReadOnlyList<PluginSetting> Fields, IPluginSettings Settings);

internal sealed partial class PluginManager
{
    private IPluginSettings SettingsFor(Entry entry)
    {
        if (entry.Settings is { } settings) return settings;
        var external = entry.Registration.Definition.Metadata?.External;
        if (external is not null) PluginSettingsMigration.EnsureMigrated(external);
        var store = external is null ? _store : new SettingsStore(PluginConsentPolicy.HostSettingsPath(external));
        return entry.Settings = new PluginSettings(store, entry.Registration.Definition.Id);
    }

    /// <summary>外部插件只从已授权的运行实例读取设置声明；停用时不执行第三方代码。</summary>
    public async Task<PluginSettingsModel?> GetSettingsAsync(string id)
    {
        await _operations.WaitAsync();
        try
        {
            var entry = Entries.FirstOrDefault(item => item.Registration.Definition.Id == id);
            if (_disposed || entry is null || entry.Removed) return null;
            await entry.Cleanup;
            var external = entry.Registration.Definition.Metadata?.External;
            if (external is not null && (entry.Plugin is null || !entry.Active
                || !await Task.Run(() => PluginConsentPolicy.HasConsent(external)))) return null;
            if (entry.Schema is null)
            {
                IMessageSource? temporary = null;
                try
                {
                    var plugin = entry.Plugin ?? (temporary = entry.Registration.Create());
                    var fields = (plugin as IPluginSettingsSchema)?.SettingsSchema ?? [];
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    var snapshot = new List<PluginSetting>();
                    foreach (var field in fields)
                    {
                        SettingsStore.ValidatePluginKey(field.Key);
                        if (!keys.Add(field.Key) || string.IsNullOrWhiteSpace(field.Title)
                            || (field.Type == PluginSettingType.String ? field.DefaultValue is not null and not string
                                : field.Type != PluginSettingType.Boolean || field.DefaultValue is not bool))
                            throw new InvalidDataException("Invalid plugin settings schema.");
                        // Only contract data survives this call; no plugin types or delegates retain the ALC.
                        snapshot.Add(new(field.Key, field.Type, field.Title, field.DefaultValue));
                    }
                    entry.Schema = snapshot.ToArray();
                }
                catch (Exception error) { Diagnostic(entry).Capture(error, "SettingsSchema"); return null; }
                finally
                {
                    if (temporary is not null)
                    {
                        try { temporary.Dispose(); }
                        catch (Exception error) { Diagnostic(entry).Capture(error, "Dispose"); }
                        entry.Registration.Definition.Metadata?.External?.Unload();
                    }
                }
            }
            return new(entry.Schema, SettingsFor(entry));
        }
        finally { _operations.Release(); }
    }

    // A disabled plugin has no running instance. Initialize a cleanup-only instance for uninstall,
    // without subscriptions, notification delivery, pinned content, or StartAsync.
    private async Task PrepareLifecycleAsync(Entry entry)
    {
        await entry.Cleanup;
        if (entry.Plugin is not null) return;
        // 未运行的外部插件不能为更新或卸载而创建 worker/调用构造函数。
        if (entry.Registration.Definition.Metadata?.External is not null) return;
        try
        {
            var plugin = entry.Registration.Create();
            entry.Plugin = plugin;
            if (plugin is not IPluginLifecycleAware || plugin is LifecycleSource { HasLifecycleHooks: false }) return;
            var external = entry.Registration.Definition.Metadata?.External;
            var trust = external?.Trust;
            entry.Context = new PluginContext(plugin.Id, SettingsFor(entry), Diagnostic(entry), _ => { }, type =>
                type == typeof(IPluginPermissions) ? trust
                : type == typeof(IPluginStorage) && trust?.IsGranted(PluginPermissions.Settings) == true && external is not null
                    ? new PluginStorage(external.DataDirectory) : null, previousVersion: entry.PreviousVersion);
            await plugin.InitializeAsync(entry.Context);
        }
        catch (Exception error) { Diagnostic(entry).Capture(error, "UninstallInitialize"); }
    }
}
