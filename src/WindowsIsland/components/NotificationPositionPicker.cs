using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class NotificationPositionPicker : Grid
{
    private readonly Dictionary<NotificationPosition, ToggleButton> _buttons = [];
    private NotificationPosition _selected;
    private bool _syncing;
    public event Action<NotificationPosition>? Selected;

    public NotificationPositionPicker()
    {
        RowSpacing = ColumnSpacing = 4;
        for (var index = 0; index < 3; index++)
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        foreach (var position in Enum.GetValues<NotificationPosition>())
        {
            var anchor = NotificationPlacement.Anchor(position);
            var direction = NotificationPlacement.Outward(position);
            var icon = new SymbolIcon(Symbol.Up)
            {
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
                RenderTransform = new RotateTransform { Angle = Math.Atan2(direction.X, -direction.Y) * 180 / Math.PI }
            };
            var button = new ToggleButton { Content = icon, Width = 36, Height = 32, Padding = new Thickness(0) };
            AutomationProperties.SetAutomationId(button, "NotificationPosition" + position);
            AutomationProperties.SetName(button, NotificationPlacement.Label(position));
            ToolTipService.SetToolTip(button, NotificationPlacement.Label(position));
            button.Checked += (_, _) => { if (!_syncing) { Select(position); Selected?.Invoke(position); } };
            button.Unchecked += (_, _) => { if (!_syncing && position == _selected) Select(_selected); };
            SetColumn(button, (int)(anchor.X * 2));
            SetRow(button, (int)(anchor.Y * 2));
            Children.Add(button);
            _buttons.Add(position, button);
        }
        AutomationProperties.SetAutomationId(this, "NotificationPositionPicker");
    }

    public void Select(NotificationPosition position)
    {
        position = NotificationPlacement.Normalize(position);
        _selected = position;
        _syncing = true;
        try { foreach (var (value, button) in _buttons) button.IsChecked = value == position; }
        finally { _syncing = false; }
    }

    public void SetEnabled(bool enabled) { foreach (var button in _buttons.Values) button.IsEnabled = enabled; }
}
