using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace WindowsIsland.Components;

internal sealed class SettingsLinkRow : StackPanel
{
    public SettingsRow Header { get; }
    public event Action? OpenRequested;

    public SettingsLinkRow(IconElement icon, string title, string automationId, string? statusId = null,
        bool localizeTitle = true, params FrameworkElement[] actions)
    {
        var open = new IconActionButton(0xE76C, "ActionOpenPlugin", automationId + ".Open");
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (var action in actions) toolbar.Children.Add(action);
        toolbar.Children.Add(open);
        Header = new SettingsRow(icon, title, toolbar, statusId, localizeTitle);
        Header.Tapped += (_, args) =>
        {
            for (var element = args.OriginalSource as DependencyObject; element is not null && element != Header;
                element = VisualTreeHelper.GetParent(element))
                if (element is ButtonBase or ToggleSwitch) return;
            OpenRequested?.Invoke();
            args.Handled = true;
        };
        open.Click += (_, _) => OpenRequested?.Invoke();
        Children.Add(Header);
        AutomationProperties.SetAutomationId(this, automationId);
    }
}
