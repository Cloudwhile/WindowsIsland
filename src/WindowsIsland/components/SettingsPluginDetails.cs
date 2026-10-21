using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class SettingsPluginDetails : SettingsContent, IDisposable
{
    private readonly PluginMetadata? _metadata;
    public bool Matches(NotificationPluginDefinition definition) => ReferenceEquals(_metadata, definition.Metadata);
    private readonly TextBlock _size;
    private readonly PluginCatalogNotice _catalogNotice;
    private bool _reading, _read, _disposed;
    private long? _bytes;
    private readonly Func<string, Task<PluginSettingsModel?>>? _settings;
    private readonly string _id;

    /// <summary>创建可选择和复制的插件详情。</summary>
    public SettingsPluginDetails(NotificationPluginDefinition definition, PluginCatalogService? catalog = null,
        Func<string, Task<PluginSettingsModel?>>? settings = null) : base(false)
    {
        _id = definition.Id;
        _settings = settings;
        _metadata = definition.Metadata;
        Field("PluginVersion", _metadata?.Version, definition.Id + ".Version");
        var type = Field("PluginType", null, definition.Id + ".Type", placeholder: false);
        LocalizedUI.Bind(type, () => type.Text = Localization.Get(_metadata?.IsBuiltIn != false ? "PluginBuiltIn" : "PluginInstalled"));
        Field("PluginAuthor", _metadata?.Author, definition.Id + ".Author");
        Field("PluginIdentifier", definition.Id, definition.Id + ".Id");
        if (_metadata?.External is { } external)
        {
            var source = Field("PluginSource", null, definition.Id + ".Source", placeholder: false);
            LocalizedUI.Bind(source, () => source.Text = Localization.Get(external.Manifest.SourceKind == Contracts.PluginSourceKind.Manual ? "PluginManualRestricted" : "PluginOfficial"));
            Field("PluginDeveloper", external.Manifest.DeveloperName ?? external.Manifest.DeveloperId, definition.Id + ".Developer");
            Field("PluginRepository", external.Manifest.RepositoryUrl, definition.Id + ".Repository");
            var trust = external.Trust;
            var granted = PermissionField("PluginPermissionsGranted", trust.Granted, definition.Id + ".Granted");
            var denied = PermissionField("PluginPermissionsDenied", trust.Denied, definition.Id + ".Denied");
            var permissions = new SettingsColumns(granted, denied);
            AutomationProperties.SetAutomationId(permissions, "PluginPermissions." + definition.Id);
            Children.Add(permissions);
            var notice = new SettingsField(null, "", placeholder: false);
            LocalizedUI.Bind(notice, () => notice.Value.Text = Localization.Get("PluginPermissionNotice"));
            Children.Add(notice);
        }
        Field("PluginLocation", _metadata?.Location, definition.Id + ".Location");
        _size = Field("PluginFileSize", null, definition.Id + ".Size", placeholder: false);
        LocalizedUI.Bind(_size, RefreshSize);
        _catalogNotice = new PluginCatalogNotice(catalog, _metadata?.External?.Manifest);
        AutomationProperties.SetAutomationId(_catalogNotice, "PluginCatalogNotice." + definition.Id);
        Children.Add(_catalogNotice);
        RefreshCatalog();
    }

    public void RefreshCatalog() => _catalogNotice.Refresh();

    private static SettingsField PermissionField(string label, IReadOnlyCollection<string> values, string id)
    {
        var field = new SettingsField(label, string.Join(", ", values), "PluginDetail." + id, placeholder: false);
        if (values.Count == 0) LocalizedUI.Bind(field, () => field.Value.Text = Localization.Get("PluginPermissionsNone"));
        return field;
    }

    private TextBlock Field(string label, string? value, string id, bool placeholder = true)
    {
        var group = new SettingsField(label, value, "PluginDetail." + id, placeholder);
        Children.Add(group);
        return group.Value;
    }

    /// <summary>首次展开时异步读取大小，组件释放后忽略晚到结果。</summary>
    public async Task ReadSizeAsync()
    {
        if (_disposed || _reading || _read) return;
        _reading = true;
        try
        {
            var bytes = _metadata is null ? null : await _metadata.ReadSizeAsync();
            var model = _settings is null ? null : await _settings(_id);
            if (_disposed) return;
            if (model is { Fields.Count: > 0 }) Children.Add(new PluginSettingsEditor(_id, model));
            _bytes = bytes;
            _read = true;
            RefreshSize();
        }
        catch (Exception error) { new PluginDiagnostics(_id, DiagnosticArea.Application).Capture(error, "SettingsSchema"); }
        finally { _reading = false; }
    }

    private void RefreshSize() => _size.Text = _bytes is { } bytes
        ? Localization.Format("PluginSizeBytes", bytes.ToString("N0", Localization.Culture)) : Localization.Get("PluginUnknown");

    /// <summary>停止接收尚未完成的详情读取结果。</summary>
    public void Dispose() => _disposed = true;
}
