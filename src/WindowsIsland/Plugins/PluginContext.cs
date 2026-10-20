using WindowsIsland.Contracts;
using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

internal sealed class PluginContext(string id, IPluginSettings settings, IPluginDiagnostics diagnostics,
    Action<PluginNotification> publish, Func<Type, object?>? services = null) : IPluginContext, IDisposable
{
    private int _disposed;
    public string PluginId => id;
    public IPluginSettings Settings => settings;
    public IPluginDiagnostics Diagnostics => diagnostics;
    public T? GetService<T>() where T : class => Volatile.Read(ref _disposed) == 0 ? services?.Invoke(typeof(T)) as T : null;
    public void PublishNotification(PluginNotification notification)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        publish(notification);
    }
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}

internal sealed class PluginSettings(SettingsStore store, string pluginId) : IPluginSettings
{
    public string? GetString(string key, string? defaultValue = null) => store.GetPluginString(pluginId, key, defaultValue);
    public bool GetBoolean(string key, bool defaultValue = false) => store.GetPluginBoolean(pluginId, key, defaultValue);
    public void SetString(string key, string? value) => store.SetPluginString(pluginId, key, value);
    public void SetBoolean(string key, bool value) => store.SetPluginBoolean(pluginId, key, value);
}

internal sealed class PluginDiagnostics(string id, DiagnosticArea area) : IPluginDiagnostics
{
    private DiagnosticArea Area => id switch
    {
        "system" => DiagnosticArea.SystemPlugin,
        "telegram" => DiagnosticArea.TelegramPlugin,
        "power" => DiagnosticArea.PowerPlugin,
        "wechat" => DiagnosticArea.WeChatPlugin,
        _ => area
    };
    public void Write(PluginDiagnostic diagnostic) => WindowsIsland.Services.Diagnostics.Store?.RecordPlugin(id, Area,
        diagnostic.Operation, fatal: diagnostic.IsFatal, information: diagnostic.AdditionalInformation);

    public void Capture(Exception error, string operation) => WindowsIsland.Services.Diagnostics.Store?.RecordPlugin(id, Area,
        operation, error);
}
