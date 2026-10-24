using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WindowsIsland.Components;

/// <summary>维护页的图标操作区，按实际可用宽度换行。</summary>
internal sealed class SettingsMaintenanceActions : Grid
{
    private readonly FrameworkElement[] _actions;
    private int _columns = -1;
    private int _visibleCount = -1;

    public SettingsMaintenanceActions(params FrameworkElement[] actions)
    {
        _actions = actions;
        ColumnSpacing = RowSpacing = SettingsTokens.ControlSpacing;
        foreach (var action in actions)
        {
            Children.Add(action);
            action.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => ArrangeActions());
        }
        SizeChanged += (_, _) => ArrangeActions();
        ArrangeActions();
    }

    private void ArrangeActions()
    {
        var visible = _actions.Where(action => action.Visibility == Visibility.Visible).ToArray();
        const double actionWidth = 36;
        var columns = ActualWidth > 0
            ? Math.Max(1, (int)((ActualWidth + ColumnSpacing) / (actionWidth + ColumnSpacing)))
            : Math.Max(1, visible.Length);
        columns = Math.Min(columns, Math.Max(1, visible.Length));
        // Visibility may change while the count stays equal (for example check/cancel).
        for (var index = 0; index < visible.Length; index++)
        {
            SetColumn(visible[index], index % columns);
            SetRow(visible[index], index / columns);
        }
        if (_columns == columns && _visibleCount == visible.Length) return;
        _columns = columns;
        _visibleCount = visible.Length;
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();
        for (var index = 0; index < columns; index++)
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(actionWidth) });
        for (var index = 0; index < (visible.Length + columns - 1) / columns; index++)
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    }
}
