using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class SettingsHeader : Grid
{
    private readonly TextBlock _status = IslandTheme.Text("", 12, secondary: true);
    private readonly TextBlock _title = IslandTheme.Text("", SettingsTokens.PageTitleSize);
    private readonly IconActionButton _back = new(Symbol.Back, "ActionBack", "BackToPluginList")
    { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
    public event Action? BackRequested;
    public bool HasBack { set => _back.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    public string Status { get => _status.Text; set { if (_status.Text != value) _status.Text = value; _status.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible; } }
    public string Title { get => _title.Text; set { if (_title.Text != value) _title.Text = value; } }
    public void FocusTitle() => _back.Focus(FocusState.Programmatic);

    public SettingsHeader(params UIElement[] actions)
    {
        Margin = new Thickness(SettingsTokens.PagePadding, 12, SettingsTokens.PagePadding, 16);
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _title.FontWeight = FontWeights.SemiBold;
        _title.Text = Localization.Get("Settings");
        _title.TextWrapping = _status.TextWrapping = TextWrapping.Wrap;
        var labels = new StackPanel { Spacing = 4 };
        labels.Children.Add(_title);
        labels.Children.Add(_status);
        Children.Add(_back);
        _back.Click += (_, _) => BackRequested?.Invoke();
        Grid.SetColumn(labels, 1);
        Children.Add(labels);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = SettingsTokens.ControlSpacing,
            VerticalAlignment = VerticalAlignment.Center };
        foreach (var action in actions) toolbar.Children.Add(action);
        Grid.SetColumn(toolbar, 2);
        Children.Add(toolbar);
        AutomationProperties.SetAutomationId(_status, "SettingsReadiness");
        AutomationProperties.SetHeadingLevel(_title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
    }
}
