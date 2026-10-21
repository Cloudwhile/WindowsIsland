using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class SettingsRow : Grid
{
    // One text per tone keeps theme resources bound; the automation ID follows the visible one.
    private readonly Dictionary<StatusTone, TextBlock> _statuses = new()
    {
        [StatusTone.Neutral] = IslandTheme.Text("", 12, secondary: true),
        [StatusTone.Success] = IslandTheme.Text("", 12, "SystemFillColorSuccessBrush"),
        [StatusTone.Critical] = IslandTheme.Text("", 12, "SystemFillColorCriticalBrush")
    };
    private readonly TextBlock _detail = IslandTheme.Text("", 12, secondary: true);
    private readonly TextBlock _name;
    public string Title { get => _name.Text; set => _name.Text = value; }
    private StatusTone _tone;
    private string _status = "";

    public string Status
    {
        get => _status;
        set { _status = value ?? ""; Apply(); }
    }

    public StatusTone Tone
    {
        get => _tone;
        set { if (_tone != value) { _tone = value; Apply(); } }
    }

    public string Detail
    {
        get => _detail.Text;
        set
        {
            if (_detail.Text != value) _detail.Text = value ?? "";
            _detail.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public SettingsRow(Symbol symbol, string title, FrameworkElement action, string? statusId = null, bool localizeTitle = true)
        : this(new SymbolIcon(symbol), title, action, statusId, localizeTitle) { }

    public SettingsRow(IconElement icon, string title, FrameworkElement action, string? statusId = null, bool localizeTitle = true)
    {
        MinHeight = 52;
        Padding = new Thickness(12, 8, 12, 8);
        ColumnSpacing = 16;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        Children.Add(icon);
        var name = _name = localizeTitle ? LocalizedUI.Text(title) : IslandTheme.Text(title);
        name.TextWrapping = TextWrapping.Wrap;
        name.FontWeight = FontWeights.SemiBold;
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(name);
        foreach (var status in _statuses.Values)
        {
            status.TextWrapping = TextWrapping.Wrap;
            status.Visibility = Visibility.Collapsed;
            // Collapsed texts are absent from the automation tree, so the ID stays unique.
            if (statusId is not null) AutomationProperties.SetAutomationId(status, statusId);
            text.Children.Add(status);
        }
        _detail.TextWrapping = TextWrapping.Wrap;
        _detail.Visibility = Visibility.Collapsed;
        if (statusId is not null) AutomationProperties.SetAutomationId(_detail, statusId + ".Detail");
        text.Children.Add(_detail);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(action, 2);
        Children.Add(text);
        Children.Add(action);
        var divider = (Border)XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource DividerStrokeColorDefaultBrush}' />");
        divider.Height = 1;
        divider.Margin = new Thickness(40, 0, 0, -8);
        divider.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumnSpan(divider, 3);
        Children.Add(divider);
    }

    private void Apply()
    {
        foreach (var (tone, text) in _statuses)
        {
            if (text.Text != _status) text.Text = _status;
            text.Visibility = tone == _tone && _status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
