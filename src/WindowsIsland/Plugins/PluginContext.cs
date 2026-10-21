using WindowsIsland.Contracts;
using WindowsIsland.Services;

namespace WindowsIsland.Plugins;

internal sealed class PluginContext(string id, IPluginSettings settings, IPluginDiagnostics diagnostics,
    Action<PluginNotification> publish, Func<Type, object?>? services = null,
    Action<PinnedContentChange>? pinned = null) : IPluginContext, IPinnedContentSource, IDisposable
{
    private int _disposed;
    private Func<PinnedControlInteraction, Task>? _interaction;
    private readonly RevocablePluginSettings _settings = new(settings);
    public string PluginId => id;
    public IPluginSettings Settings => _settings;
    public IPluginDiagnostics Diagnostics => diagnostics;
    public T? GetService<T>() where T : class => Volatile.Read(ref _disposed) != 0 ? null
        : typeof(T) == typeof(IPinnedContentSource) && pinned is not null ? this as T : services?.Invoke(typeof(T)) as T;
    public void Submit(PinnedContent content) => PublishPinned(new(PinnedChangeKind.Added, id, content.Id, content));
    public void Update(PinnedContent content) => PublishPinned(new(PinnedChangeKind.Updated, id, content.Id, content));
    public void Remove(string contentId) => PublishPinned(new(PinnedChangeKind.Removed, id, contentId));
    public void SetInteractionHandler(Func<PinnedControlInteraction, Task>? handler)
    {
        if (Volatile.Read(ref _disposed) == 0) Volatile.Write(ref _interaction, handler);
        if (Volatile.Read(ref _disposed) != 0) Interlocked.Exchange(ref _interaction, null);
    }
    public Task HandleInteractionAsync(PinnedControlInteraction interaction) => Volatile.Read(ref _disposed) == 0
        ? Volatile.Read(ref _interaction)?.Invoke(interaction) ?? Task.CompletedTask : Task.CompletedTask;
    private void PublishPinned(PinnedContentChange change)
    {
        if (Volatile.Read(ref _disposed) == 0) pinned?.Invoke(change);
    }
    public void PublishNotification(PluginNotification notification)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        publish(notification);
    }
    public void Dispose() { Interlocked.Exchange(ref _disposed, 1); Interlocked.Exchange(ref _interaction, null); _settings.Revoke(); }
}

internal sealed class RevocablePluginSettings(IPluginSettings settings) : IPluginSettings
{
    private int _revoked;
    public void Revoke() => Interlocked.Exchange(ref _revoked, 1);
    public string? GetString(string key, string? defaultValue = null) => Volatile.Read(ref _revoked) == 0 ? settings.GetString(key, defaultValue) : defaultValue;
    public bool GetBoolean(string key, bool defaultValue = false) => Volatile.Read(ref _revoked) == 0 ? settings.GetBoolean(key, defaultValue) : defaultValue;
    public void SetString(string key, string? value) { if (Volatile.Read(ref _revoked) == 0) settings.SetString(key, value); }
    public void SetBoolean(string key, bool value) { if (Volatile.Read(ref _revoked) == 0) settings.SetBoolean(key, value); }
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
        BuiltInPluginIds.System => DiagnosticArea.SystemPlugin,
        BuiltInPluginIds.Telegram => DiagnosticArea.TelegramPlugin,
        BuiltInPluginIds.Power => DiagnosticArea.PowerPlugin,
        BuiltInPluginIds.WeChat => DiagnosticArea.WeChatPlugin,
        BuiltInPluginIds.Countdown => DiagnosticArea.CountdownPlugin,
        _ => area
    };
    public void Write(PluginDiagnostic diagnostic) => WindowsIsland.Services.Diagnostics.Store?.RecordPlugin(id, Area,
        diagnostic.Operation, fatal: diagnostic.IsFatal, information: diagnostic.AdditionalInformation);

    public void Capture(Exception error, string operation) => WindowsIsland.Services.Diagnostics.Store?.RecordPlugin(id, Area,
        operation, error);
}
