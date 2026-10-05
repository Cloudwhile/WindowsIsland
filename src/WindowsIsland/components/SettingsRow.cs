using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace WindowsIsland.Components;

internal sealed class SettingsRow : Grid
{
    private readonly TextBlock _status = IslandTheme.Text("", 12, secondary: true);
    public string Status
    {
        get => _status.Text;
        set
        {
            if (_status.Text != value) _status.Text = value;
            _status.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public SettingsRow(Symbol symbol, string title, FrameworkElement action, string? statusId = null, bool localizeTitle = true)
        : this(new SymbolIcon(symbol), title, action, statusId, localizeTitle) { }

    public SettingsRow(IconElement icon, string title, FrameworkElement action, string? statusId = null, bool localizeTitle = true)
    {
        MinHeight = 64;
        Padding = new Thickness(12, 10, 12, 10);
        ColumnSpacing = 16;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        Children.Add(icon);
        var name = localizeTitle ? LocalizedUI.Text(title) : IslandTheme.Text(title);
        name.TextWrapping = TextWrapping.Wrap;
        _status.TextWrapping = TextWrapping.Wrap;
        if (statusId is not null) AutomationProperties.SetAutomationId(_status, statusId);
        name.FontWeight = FontWeights.SemiBold;
        _status.Visibility = Visibility.Collapsed;
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(name);
        text.Children.Add(_status);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(action, 2);
        Children.Add(text);
        Children.Add(action);
        var divider = (Border)XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource DividerStrokeColorDefaultBrush}' />");
        divider.Height = 1;
        divider.Margin = new Thickness(40, 0, 0, -10);
        divider.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumnSpan(divider, 3);
        Children.Add(divider);
    }
}
