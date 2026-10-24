using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Windows.System;
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
            var button = new ToggleButton { Content = icon, Width = 40, Height = 40, Padding = new Thickness(0) };
            AutomationProperties.SetAutomationId(button, "NotificationPosition" + position);
            LocalizedUI.Bind(button, () =>
            {
                var label = NotificationPlacement.Label(position);
                AutomationProperties.SetName(button, label);
                ToolTipService.SetToolTip(button, label);
            });
            button.Checked += (_, _) => { if (!_syncing) { Select(position); Selected?.Invoke(position); } };
            button.Unchecked += (_, _) => { if (!_syncing && position == _selected) Select(_selected); };
            SetColumn(button, (int)(anchor.X * 2));
            SetRow(button, (int)(anchor.Y * 2));
            Children.Add(button);
            _buttons.Add(position, button);
        }
        AutomationProperties.SetAutomationId(this, "NotificationPositionPicker");
        LocalizedUI.Bind(this, () => AutomationProperties.SetName(this, Localization.Get("NotificationPosition")));
        KeyDown += OnKeyDown;
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

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        var origin = args.OriginalSource as DependencyObject;
        while (origin is not null && origin is not ToggleButton) origin = VisualTreeHelper.GetParent(origin);
        var current = _buttons.FirstOrDefault(item => ReferenceEquals(item.Value, origin));
        if (current.Value is null) return;
        var anchor = NotificationPlacement.Anchor(current.Key);
        var column = (int)(anchor.X * 2);
        var row = (int)(anchor.Y * 2);
        var offset = args.Key switch
        {
            VirtualKey.Left => (-1, 0), VirtualKey.Right => (1, 0),
            VirtualKey.Up => (0, -1), VirtualKey.Down => (0, 1), _ => (0, 0)
        };
        if (offset == (0, 0)) return;
        args.Handled = true;
        for (var step = 1; step <= 2; step++)
        {
            var nextColumn = column + offset.Item1 * step;
            var nextRow = row + offset.Item2 * step;
            var target = _buttons.FirstOrDefault(item =>
            {
                var targetAnchor = NotificationPlacement.Anchor(item.Key);
                return (int)(targetAnchor.X * 2) == nextColumn && (int)(targetAnchor.Y * 2) == nextRow;
            });
            if (target.Value is not { IsEnabled: true }) continue;
            Select(target.Key);
            target.Value.Focus(FocusState.Keyboard);
            Selected?.Invoke(target.Key);
            return;
        }
    }
}
