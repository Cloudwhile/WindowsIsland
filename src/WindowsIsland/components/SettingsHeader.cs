using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class SettingsHeader : Grid
{
    private readonly TextBlock _status = IslandTheme.Text("", 12, secondary: true);
    private readonly TextBlock _title = IslandTheme.Text("", 28);
    private readonly IconActionButton _back = new(Symbol.Back, "ActionBack", "BackToPluginList")
    { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
    public event Action? BackRequested;
    public bool HasBack { set => _back.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    public string Status { get => _status.Text; set => _status.Text = value; }
    public string Title { get => _title.Text; set => _title.Text = value; }

    public SettingsHeader(params UIElement[] actions)
    {
        Margin = new Thickness(16, 8, 16, 8);
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
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4,
            Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var action in actions) toolbar.Children.Add(action);
        Grid.SetColumn(toolbar, 2);
        Children.Add(toolbar);
        AutomationProperties.SetAutomationId(_status, "SettingsReadiness");
    }
}
