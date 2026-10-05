using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class SettingsNavigation : Grid, IDisposable
{
    private readonly Dictionary<string, NavigationViewItem> _items = [];
    private readonly NavigationView _view = new()
    {
        PaneDisplayMode = NavigationViewPaneDisplayMode.Auto,
        OpenPaneLength = 176,
        CompactModeThresholdWidth = 0,
        ExpandedModeThresholdWidth = 760,
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        IsSettingsVisible = false,
        AlwaysShowHeader = false,
        IsTitleBarAutoPaddingEnabled = false
    };
    public UIElement? Content { get => _view.Content as UIElement; set => _view.Content = value; }
    public event Action<string>? SectionSelected;

    public SettingsNavigation()
    {
        Children.Add(_view);
        Add("setup", "Setup", Symbol.Permissions);
        Add("sources", "Sources", Symbol.Message);
        Add("appearance", "Appearance", Symbol.Map);
        Add("updates", "Updates", Symbol.Download);
        _view.SelectionChanged += OnSelectionChanged;
        AutomationProperties.SetAutomationId(this, "SettingsNavigation");
        LocalizedUI.Label(this, "SettingsNavigation");
    }

    private void Add(string section, string label, Symbol icon)
    {
        var item = new NavigationViewItem { Icon = new SymbolIcon(icon), Tag = section };
        AutomationProperties.SetAutomationId(item, "SettingsNav" + section);
        LocalizedUI.Bind(item, () => item.Content = Localization.Get(label));
        LocalizedUI.Label(item, label);
        _items.Add(section, item);
        _view.MenuItems.Add(item);
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string section }) SectionSelected?.Invoke(section);
    }

    public void Select(string section) => _view.SelectedItem = _items[section];

    public void Dispose()
    {
        _view.SelectionChanged -= OnSelectionChanged;
        SectionSelected = null;
    }
}
