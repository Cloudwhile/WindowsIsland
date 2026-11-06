using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class SettingsNavigation : Grid, IDisposable
{
    private readonly Dictionary<string, NavigationViewItem> _items = [];
    private bool _disposed;
    private readonly NavigationView _view = new()
    {
        PaneDisplayMode = NavigationViewPaneDisplayMode.Auto,
        OpenPaneLength = SettingsTokens.NavigationWidth,
        // Keep the native icon rail available even when the content header is custom.
        CompactModeThresholdWidth = 0,
        ExpandedModeThresholdWidth = 780,
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
        AddGroup("NavigationRuntime");
        Add("overview", "Overview", Symbol.ViewAll);
        Add("sources", "Sources", Symbol.Message);
        Add("plugins", "PluginManagement", Symbol.AllApps);
        AddGroup("NavigationPreferences");
        Add("appearance", "Appearance", new FontIcon { Glyph = char.ConvertFromUtf32(0xE790) }); // Color palette
        Add("behavior", "NotificationBehavior", new FontIcon { Glyph = char.ConvertFromUtf32(0xE7F4) });
        AddGroup("NavigationMaintenance");
        Add("updates", "Updates", Symbol.Download);
        Add("diagnostics", "DiagnosticsTitle", new SymbolIcon(Symbol.Document), footer: true);
        _view.SelectionChanged += OnSelectionChanged;
        _view.ItemInvoked += OnItemInvoked;
        AutomationProperties.SetAutomationId(this, "SettingsNavigation");
        LocalizedUI.SetAutomationName(_view, "SettingsNavigation");
        _view.DisplayModeChanged += OnDisplayModeChanged;
        _view.Loaded += OnViewLoaded;
        _view.PaneOpened += OnPaneStateChanged;
        _view.PaneClosed += OnPaneStateChanged;
        LocalizedUI.Bind(this, QueuePaneActionRefresh);
    }

    private void AddGroup(string key)
    {
        var header = new NavigationViewItemHeader();
        LocalizedUI.Bind(header, () => header.Content = Localization.Get(key));
        _view.MenuItems.Add(header);
    }

    private void OnDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
    {
        sender.IsPaneOpen = args.DisplayMode == NavigationViewDisplayMode.Expanded;
        sender.IsPaneToggleButtonVisible = args.DisplayMode != NavigationViewDisplayMode.Expanded;
        QueuePaneActionRefresh();
    }

    private void OnViewLoaded(object sender, RoutedEventArgs args) => QueuePaneActionRefresh();
    private void OnPaneStateChanged(NavigationView sender, object args) => QueuePaneActionRefresh();
    private void QueuePaneActionRefresh()
    {
        RefreshPaneAction();
        DispatcherQueue.TryEnqueue(RefreshPaneAction);
    }

    private void RefreshPaneAction()
    {
        if (_disposed || !_view.IsLoaded) return;
        var toggle = Descendants(_view).OfType<Button>().FirstOrDefault(button => button.Name == "TogglePaneButton");
        if (toggle is null) return;
        var label = Localization.Get("ActionToggleNavigation");
        AutomationProperties.SetName(toggle, label);
        if (ToolTipService.GetToolTip(toggle) is ToolTip tooltip) tooltip.Content = label;
        else ToolTipService.SetToolTip(toggle, new ToolTip { Content = label });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private void Add(string section, string label, Symbol icon) => Add(section, label, new SymbolIcon(icon));

    private void Add(string section, string label, IconElement icon, bool footer = false)
    {
        var item = new NavigationViewItem { Icon = icon, Tag = section };
        AutomationProperties.SetAutomationId(item, "SettingsNav" + section);
        LocalizedUI.Bind(item, () => item.Content = Localization.Get(label));
        LocalizedUI.Label(item, label);
        _items.Add(section, item);
        if (footer) _view.FooterMenuItems.Add(item);
        else _view.MenuItems.Add(item);
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string section }) SectionSelected?.Invoke(section);
    }

    private void OnItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem { Tag: string section }) SectionSelected?.Invoke(section);
    }

    public void Select(string section) => _view.SelectedItem = _items[section == "setup" ? "overview" : section];

    public void Dispose()
    {
        _disposed = true;
        _view.SelectionChanged -= OnSelectionChanged;
        _view.ItemInvoked -= OnItemInvoked;
        _view.DisplayModeChanged -= OnDisplayModeChanged;
        _view.Loaded -= OnViewLoaded;
        _view.PaneOpened -= OnPaneStateChanged;
        _view.PaneClosed -= OnPaneStateChanged;
        SectionSelected = null;
    }
}
