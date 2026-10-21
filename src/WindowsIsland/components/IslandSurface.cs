using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class IslandSurface : FlyoutPresenter, IDisposable
{
    private readonly ThemeSettings? _theme;
    private readonly UISettings _systemSettings = new();
    private readonly Brush _acrylic = (Brush)Application.Current.Resources["IslandAcrylicBrush"];
    private readonly Brush _solid = (Brush)Application.Current.Resources["IslandSolidBrush"];
    private readonly Brush _highContrast = (Brush)Application.Current.Resources["IslandHighContrastBrush"];
    private AppSettings _settings = new();
    private bool _disposed, _effectsSubscribed;

    public IslandSurface(UIElement content, WindowId windowId)
    {
        Content = content;
        try
        {
            _theme = ThemeSettings.CreateForWindowId(windowId);
            _theme.Changed += OnSystemThemeChanged;
        }
        catch (COMException) { _theme = null; }
        try
        {
            _systemSettings.AdvancedEffectsEnabledChanged += OnEffectsChanged;
            _effectsSubscribed = true;
        }
        catch (COMException) { }
        ActualThemeChanged += OnThemeChanged;
        ApplyAppearance(_settings);
        IsTabStop = false;
        AllowFocusOnInteraction = false;
        UseLayoutRounding = true;
        MaxWidth = NotificationLayout.MaxWidth;
        MaxHeight = NotificationLayout.MaxHeight;
        MinWidth = MinHeight = 0;
        CornerRadius = new CornerRadius(NotificationLayout.CornerRadius);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollMode(this, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollMode(this, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(this, ScrollBarVisibility.Disabled);
    }

    public void ApplyAppearance(AppSettings settings)
    {
        if (_disposed) return;
        _settings = settings;
        bool highContrast, effects;
        try
        {
            highContrast = _theme?.HighContrast ?? true;
            effects = !highContrast && _systemSettings.AdvancedEffectsEnabled;
        }
        catch (COMException) { highContrast = true; effects = false; }
        var opacity = effects ? AppSettings.NormalizeBackgroundOpacity(settings.BackgroundOpacity) : 1;
        _acrylic.Opacity = _solid.Opacity = opacity;
        Background = highContrast ? _highContrast : settings.Acrylic && effects ? _acrylic : _solid;
    }

    private void RefreshAppearance() => DispatcherQueue.TryEnqueue(() => ApplyAppearance(_settings));
    private void OnSystemThemeChanged(ThemeSettings sender, object args) => RefreshAppearance();
    private void OnEffectsChanged(UISettings sender, object args) => RefreshAppearance();
    private void OnThemeChanged(FrameworkElement sender, object args) => ApplyAppearance(_settings);

    public void Dispose()
    {
        _disposed = true;
        if (_theme is not null) _theme.Changed -= OnSystemThemeChanged;
        if (_effectsSubscribed) _systemSettings.AdvancedEffectsEnabledChanged -= OnEffectsChanged;
        ActualThemeChanged -= OnThemeChanged;
    }
}
