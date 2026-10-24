using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace WindowsIsland.Components;

/// <summary>设置页共用的动画策略、时长及缓动；不改动系统偏好。</summary>
internal sealed class SettingsMotion : DependencyObject
{
    private static readonly UISettings? SystemSettings = CreateSystemSettings();
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SettingsMotion), new PropertyMetadata(true, (_, _) => Changed?.Invoke()));
    public const int Milliseconds = 160;
    public static Duration Duration => new(TimeSpan.FromMilliseconds(Milliseconds));
    // 组件只在 Loaded 与 Unloaded 之间订阅，避免静态事件保留已关闭的界面。
    public static event Action? Changed;

    static SettingsMotion()
    {
        try { if (SystemSettings is not null) SystemSettings.AnimationsEnabledChanged += (_, _) => Changed?.Invoke(); }
        catch (COMException) { }
    }

    private SettingsMotion() { }
    public static void SetEnabled(FrameworkElement element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(FrameworkElement element) => (bool)element.GetValue(EnabledProperty);

    public static bool Allow(FrameworkElement element)
    {
        if (!element.IsLoaded) return false;
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement ancestor && !GetEnabled(ancestor)) return false;
        try { return Allows(element.IsLoaded, true, SystemSettings?.AnimationsEnabled == true); }
        catch (COMException) { return false; }
    }

    internal static bool Allows(bool loaded, bool applicationEnabled, bool systemAnimationsEnabled) =>
        loaded && applicationEnabled && systemAnimationsEnabled;

    public static EasingFunctionBase Easing() => new CubicEase { EasingMode = EasingMode.EaseOut };

    public static DoubleAnimation CreateAnimation(DependencyObject target, string property, double from, double to, bool dependent = false)
    {
        var animation = new DoubleAnimation
        {
            From = from, To = to, Duration = Duration, EasingFunction = Easing(), EnableDependentAnimation = dependent
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    private static UISettings? CreateSystemSettings()
    {
        try { return new UISettings(); }
        catch (COMException) { return null; }
    }
}
