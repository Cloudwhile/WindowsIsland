using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

/// <summary>列表与已安装插件详情共用的下架及更新提示。</summary>
internal sealed class PluginCatalogNotice : StackPanel
{
    private readonly TextBlock _delisted = IslandTheme.Text("", 12, "SystemFillColorCriticalBrush");
    private readonly TextBlock _update = IslandTheme.Text("", 12, secondary: true);
    private readonly PluginCatalogService? _catalog;
    private readonly PluginManifest? _manifest;

    public PluginCatalogNotice(PluginCatalogService? catalog, PluginManifest? manifest)
    {
        _catalog = catalog;
        _manifest = manifest;
        // 主题资源随明暗主题切换，更新提示保留中性色。
        _delisted.TextWrapping = _update.TextWrapping = TextWrapping.Wrap;
        _delisted.IsTextSelectionEnabled = _update.IsTextSelectionEnabled = true;
        Children.Add(_delisted);
        Children.Add(_update);
        LocalizedUI.Bind(this, Refresh);
    }

    public void Refresh()
    {
        var delisted = _manifest is not null && _catalog?.IsDelisted(_manifest) == true;
        var update = _manifest is not null ? _catalog?.UpdateFor(_manifest) : null;
        _delisted.Text = delisted ? Localization.Get("PluginDelisted") : "";
        _update.Text = !delisted && update is not null ? Localization.Format("PluginUpdateAvailable", update.Version) : "";
        _delisted.Visibility = delisted ? Visibility.Visible : Visibility.Collapsed;
        _update.Visibility = _update.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Visibility = delisted || _update.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
