using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class SettingsPluginDetails : StackPanel, IDisposable
{
    private readonly PluginMetadata? _metadata;
    public bool Matches(NotificationPluginDefinition definition) => ReferenceEquals(_metadata, definition.Metadata);
    private readonly TextBlock _size;
    private readonly PluginCatalogService? _catalog;
    private readonly TextBlock _catalogNotice = IslandTheme.Text("", 14, "SystemFillColorCriticalBrush");
    private bool _reading, _read, _disposed;
    private long? _bytes;

    /// <summary>创建可选择和复制的插件详情。</summary>
    public SettingsPluginDetails(NotificationPluginDefinition definition, PluginCatalogService? catalog = null)
    {
        _catalog = catalog;
        _metadata = definition.Metadata;
        Spacing = 10;
        Margin = new Thickness(12, 8, 12, 16);
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
            var permissions = new Grid { ColumnSpacing = 12 };
            permissions.ColumnDefinitions.Add(new ColumnDefinition());
            permissions.ColumnDefinitions.Add(new ColumnDefinition());
            var granted = new StackPanel { Spacing = 2 };
            var denied = new StackPanel { Spacing = 2 };
            granted.Children.Add(LocalizedUI.Text("PluginPermissionsGranted", 12, secondary: true));
            denied.Children.Add(LocalizedUI.Text("PluginPermissionsDenied", 12, secondary: true));
            var grantedText = IslandTheme.Text(string.Join(", ", trust.Granted), 14);
            var deniedText = IslandTheme.Text(string.Join(", ", trust.Denied), 14);
            grantedText.TextWrapping = deniedText.TextWrapping = TextWrapping.Wrap;
            granted.Children.Add(grantedText);
            denied.Children.Add(deniedText);
            Grid.SetColumn(denied, 1);
            permissions.Children.Add(granted);
            permissions.Children.Add(denied);
            Children.Add(permissions);
        }
        Field("PluginLocation", _metadata?.Location, definition.Id + ".Location");
        _size = Field("PluginFileSize", null, definition.Id + ".Size", placeholder: false);
        LocalizedUI.Bind(_size, RefreshSize);
        _catalogNotice.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetAutomationId(_catalogNotice, "PluginCatalogNotice." + definition.Id);
        Children.Add(_catalogNotice);
        RefreshCatalog();
    }

    public void RefreshCatalog()
    {
        var manifest = _metadata?.External?.Manifest;
        _catalogNotice.Text = manifest is not null && _catalog?.IsDelisted(manifest) == true ? Localization.Get("PluginDelisted")
            : manifest is not null && _catalog?.UpdateFor(manifest) is { } update ? Localization.Format("PluginUpdateAvailable", update.Version) : "";
        _catalogNotice.Visibility = _catalogNotice.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private TextBlock Field(string label, string? value, string id, bool placeholder = true)
    {
        var group = new StackPanel { Spacing = 2 };
        group.Children.Add(LocalizedUI.Text(label, 12, secondary: true));
        var text = IslandTheme.Text(value ?? "", 14);
        text.TextWrapping = TextWrapping.Wrap;
        text.IsTextSelectionEnabled = true;
        AutomationProperties.SetAutomationId(text, "PluginDetail." + id);
        if (placeholder && string.IsNullOrWhiteSpace(value)) LocalizedUI.Bind(text, () => text.Text = Localization.Get("PluginUnknown"));
        group.Children.Add(text);
        Children.Add(group);
        return text;
    }

    /// <summary>首次展开时异步读取大小，组件释放后忽略晚到结果。</summary>
    public async Task ReadSizeAsync()
    {
        if (_disposed || _reading || _read || _metadata is null) return;
        _reading = true;
        try
        {
            var bytes = await _metadata.ReadSizeAsync();
            if (_disposed) return;
            _bytes = bytes;
            _read = true;
            RefreshSize();
        }
        finally { _reading = false; }
    }

    private void RefreshSize() => _size.Text = _bytes is { } bytes
        ? Localization.Format("PluginSizeBytes", bytes.ToString("N0", Localization.Culture)) : Localization.Get("PluginUnknown");

    /// <summary>停止接收尚未完成的详情读取结果。</summary>
    public void Dispose() => _disposed = true;
}
