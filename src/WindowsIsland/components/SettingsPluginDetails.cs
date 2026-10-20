using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class SettingsPluginDetails : StackPanel, IDisposable
{
    private readonly PluginMetadata? _metadata;
    private readonly TextBlock _size;
    private bool _reading, _read, _disposed;
    private long? _bytes;

    /// <summary>创建可选择和复制的插件详情。</summary>
    public SettingsPluginDetails(NotificationPluginDefinition definition)
    {
        _metadata = definition.Metadata;
        Spacing = 10;
        Margin = new Thickness(12, 8, 12, 16);
        Field("PluginVersion", _metadata?.Version, definition.Id + ".Version");
        var type = Field("PluginType", null, definition.Id + ".Type", placeholder: false);
        LocalizedUI.Bind(type, () => type.Text = Localization.Get(_metadata?.IsBuiltIn != false ? "PluginBuiltIn" : "PluginInstalled"));
        Field("PluginAuthor", _metadata?.Author, definition.Id + ".Author");
        Field("PluginIdentifier", definition.Id, definition.Id + ".Id");
        Field("PluginLocation", _metadata?.Location, definition.Id + ".Location");
        _size = Field("PluginFileSize", null, definition.Id + ".Size", placeholder: false);
        LocalizedUI.Bind(_size, RefreshSize);
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
