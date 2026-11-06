using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WindowsIsland.Components;

/// <summary>根据内容可用宽度将两栏重排为上下两行。</summary>
internal sealed class SettingsColumns : Grid
{
    private readonly FrameworkElement _second;
    private bool _wide;

    public SettingsColumns(FrameworkElement first, FrameworkElement second)
    {
        RowSpacing = 16;
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(first);
        Children.Add(_second = second);
        SetRow(second, 1);
        SizeChanged += (_, args) => Reflow(args.NewSize.Width >= 480);
    }

    private void Reflow(bool wide)
    {
        if (_wide == wide) return;
        _wide = wide;
        ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ColumnSpacing = wide ? 16 : 0;
        RowSpacing = wide ? 0 : 16;
        SetColumn(_second, wide ? 1 : 0);
        SetRow(_second, wide ? 0 : 1);
    }
}
