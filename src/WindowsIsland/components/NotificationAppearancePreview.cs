using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

/// <summary>独立的位置选择及显示器示意；真实通知仍由宿主的预览操作创建。</summary>
internal sealed class NotificationAppearancePreview : SettingsContent, IDisposable
{
    private readonly record struct SystemAppearance(bool HighContrast, bool Effects, bool Animations);
    private const double SampleWidth = 156, SampleHeight = 42, ScreenInset = 12;
    private readonly SettingsStore _store;
    private readonly NotificationPositionPicker _picker;
    private readonly UISettings _system = new();
    private readonly AccessibilitySettings _accessibility = new();
    private readonly Grid _layout = new() { RowSpacing = SettingsTokens.RowPadding };
    private readonly Canvas _screen = new() { Height = 168 };
    private readonly Border _sample;
    private readonly Border _sampleBackground;
    private readonly Brush _acrylic, _solid, _highContrast;
    private readonly TranslateTransform _position = new();
    private readonly TextBlock _positionLabel = IslandTheme.Text("", SettingsTokens.BodyTextSize);
    private readonly IconActionButton _actualPreview = new(0xE7E7, "ActionPreviewActual", "PreviewActualNotification");
    private readonly Action _preview;
    private readonly FrameworkElement _positionControls;
    private Storyboard? _animation;
    private AppSettings? _applied;
    private SystemAppearance _systemAppearance = new(true, false, false);
    private bool _disposed, _observing, _effectsSubscribed, _animationsSubscribed, _contrastSubscribed, _wide;

    public NotificationAppearancePreview(SettingsStore store, Action preview)
        : this(store, new NotificationPositionPicker(), preview) { }

    public NotificationAppearancePreview(SettingsStore store, NotificationPositionPicker picker, Action preview)
    {
        _store = store;
        _picker = picker;
        _preview = preview;
        _positionLabel.TextWrapping = TextWrapping.Wrap;
        _positionLabel.TextTrimming = TextTrimming.None;
        var heading = new Grid { ColumnSpacing = SettingsTokens.ControlSpacing };
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(LocalizedUI.Text("AppearanceSimulatedPreview", SettingsTokens.DescriptionTextSize, secondary: true));
        Grid.SetColumn(_actualPreview, 1);
        heading.Children.Add(_actualPreview);
        Children.Add(heading);

        var display = (Border)XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                Background="{ThemeResource LayerFillColorAltBrush}"
                BorderBrush="{ThemeResource ControlStrokeColorDefaultBrush}"
                BorderThickness="1" CornerRadius="8" Padding="6" />
            """);
        display.Child = _screen;
        var monitor = new StackPanel { Spacing = 4 };
        monitor.Children.Add(display);
        var stand = (Border)XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                Background="{ThemeResource ControlStrokeColorDefaultBrush}"
                Width="48" Height="4" CornerRadius="2" HorizontalAlignment="Center" />
            """);
        monitor.Children.Add(stand);
        _layout.ColumnDefinitions.Add(new ColumnDefinition());
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.Children.Add(monitor);
        _positionControls = new StackPanel
        {
            Spacing = SettingsTokens.RowPadding,
            Children = { _positionLabel, _picker }
        };
        _picker.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetRow(_positionControls, 1);
        _layout.Children.Add(_positionControls);
        Children.Add(_layout);
        Children.Add(LocalizedUI.Text("AppearancePreviewHint", SettingsTokens.DescriptionTextSize, secondary: true));

        _sampleBackground = (Border)XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" CornerRadius="20">
                <Border.Resources>
                    <AcrylicBrush x:Key="PreviewAcrylic" TintColor="{ThemeResource SystemChromeAltHighColor}"
                        TintOpacity="0.68" FallbackColor="{ThemeResource SystemChromeMediumColor}" />
                    <SolidColorBrush x:Key="PreviewSolid" Color="{ThemeResource SystemChromeMediumColor}" />
                    <SolidColorBrush x:Key="PreviewHighContrast" Color="{ThemeResource SystemColorWindowColor}" />
                </Border.Resources>
            </Border>
            """);
        _acrylic = (Brush)_sampleBackground.Resources["PreviewAcrylic"];
        _solid = (Brush)_sampleBackground.Resources["PreviewSolid"];
        _highContrast = (Brush)_sampleBackground.Resources["PreviewHighContrast"];
        var sampleContent = new Grid { Padding = new Thickness(12, 0, 12, 0), ColumnSpacing = 8 };
        sampleContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sampleContent.ColumnDefinitions.Add(new ColumnDefinition());
        sampleContent.Children.Add(new SymbolIcon(Symbol.Message) { Width = 20, Height = 20 });
        var title = LocalizedUI.Text("PreviewTitle", SettingsTokens.DescriptionTextSize);
        title.TextWrapping = TextWrapping.NoWrap;
        Grid.SetColumn(title, 1);
        sampleContent.Children.Add(title);
        var sampleLayers = new Grid();
        sampleLayers.Children.Add(_sampleBackground);
        sampleLayers.Children.Add(sampleContent);
        _sample = (Border)XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                BorderBrush="{ThemeResource ControlStrokeColorDefaultBrush}"
                BorderThickness="1" CornerRadius="21" />
            """);
        _sample.Width = SampleWidth;
        _sample.Height = SampleHeight;
        _sample.RenderTransform = _position;
        _sample.Child = sampleLayers;
        _screen.Children.Add(_sample);
        AutomationProperties.SetAccessibilityView(_screen, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        AutomationProperties.SetAutomationId(this, "NotificationAppearancePreview");
        AutomationProperties.SetAutomationId(_sample, "AppearancePreviewIsland");
        AutomationProperties.SetAutomationId(_positionLabel, "AppearancePreviewPosition");
        _actualPreview.Click += OnPreview;
        _store.Changed += OnSettingsChanged;
        _screen.SizeChanged += OnScreenSizeChanged;
        _layout.SizeChanged += OnLayoutSizeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        LocalizedUI.Bind(this, () => Refresh(_actualPreview.IsEnabled));
        Refresh();
    }

    public void Refresh(bool enabled = true)
    {
        if (_disposed) return;
        _actualPreview.IsEnabled = enabled;
        _picker.SetEnabled(enabled);
        var settings = _store.Current;
        _picker.Select(settings.Position);
        _positionLabel.Text = Localization.Format("AppearancePositionCurrent", NotificationPlacement.Label(settings.Position));
        var first = _applied is null;
        var changed = _applied is not null && (_applied.Position != settings.Position
            || _applied.BackgroundOpacity != settings.BackgroundOpacity || _applied.Acrylic != settings.Acrylic
            || _applied.Animations != settings.Animations || _applied.OpacityTransitions != settings.OpacityTransitions);
        _applied = settings;
        var systemAppearance = ReadSystemAppearance();
        var systemChanged = systemAppearance != _systemAppearance;
        _systemAppearance = systemAppearance;
        if (first || changed || systemChanged) Present(changed && IsLoaded && !systemChanged);
    }

    private void Present(bool animate)
    {
        if (_disposed || _applied is not { } settings) return;
        var highContrast = _systemAppearance.HighContrast;
        var effects = _systemAppearance.Effects;
        var animations = _systemAppearance.Animations && settings.Animations;
        var opacity = effects ? AppSettings.NormalizeBackgroundOpacity(settings.BackgroundOpacity) : 1;
        _sampleBackground.Background = highContrast ? _highContrast : settings.Acrylic && effects ? _acrylic : _solid;
        var width = Math.Max(1, Math.Min(SampleWidth, _screen.ActualWidth - ScreenInset * 2));
        _sample.Width = width;
        var target = NotificationPlacement.Offset(settings.Position,
            new Vector2((float)_screen.ActualWidth, (float)_screen.ActualHeight),
            new Vector2((float)width, (float)SampleHeight), (float)ScreenInset);
        var fromX = _position.X;
        var fromY = _position.Y;
        var fromOpacity = _sampleBackground.Opacity;
        _animation?.Stop();
        _animation = null;
        _position.X = target.X;
        _position.Y = target.Y;
        _sampleBackground.Opacity = opacity;
        if (!animate || !animations) return;
        var storyboard = new Storyboard();
        Animate(storyboard, _position, "X", fromX, target.X);
        Animate(storyboard, _position, "Y", fromY, target.Y);
        if (settings.OpacityTransitions) Animate(storyboard, _sampleBackground, "Opacity", fromOpacity, opacity);
        if (storyboard.Children.Count == 0) return;
        _animation = storyboard;
        storyboard.Begin();
    }

    private static void Animate(Storyboard storyboard, DependencyObject target, string property, double from, double to)
    {
        if (Math.Abs(from - to) < 0.001) return;
        var animation = new DoubleAnimation
        {
            From = from, To = to, Duration = new Duration(TimeSpan.FromMilliseconds(160)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var wide = args.NewSize.Width >= 480;
        if (_wide == wide) return;
        _wide = wide;
        _layout.ColumnSpacing = wide ? SettingsTokens.SectionSpacing : 0;
        _layout.ColumnDefinitions[1].Width = wide ? new GridLength(160) : new GridLength(0);
        Grid.SetColumn(_positionControls, wide ? 1 : 0);
        Grid.SetRow(_positionControls, wide ? 0 : 1);
    }

    private void OnScreenSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var height = Math.Clamp(args.NewSize.Width * 0.56, 144, 216);
        if (Math.Abs(_screen.Height - height) > 0.5) _screen.Height = height;
        _screen.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, args.NewSize.Width, args.NewSize.Height) };
        Present(false);
    }

    private void OnPreview(object sender, RoutedEventArgs args) => _preview();
    private void OnSettingsChanged(AppSettings _) => QueueRefresh();
    private void QueueRefresh(bool systemChanged = false)
    {
        if (_disposed) return;
        void Apply()
        {
            if (_disposed) return;
            Refresh(_actualPreview.IsEnabled);
            if (systemChanged) Present(false);
        }
        if (DispatcherQueue.HasThreadAccess) Apply();
        else DispatcherQueue.TryEnqueue(Apply);
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_disposed || _observing) return;
        _observing = true;
        _effectsSubscribed = TrySubscribe(() => _system.AdvancedEffectsEnabledChanged += OnEffectsChanged);
        _animationsSubscribed = TrySubscribe(() => _system.AnimationsEnabledChanged += OnAnimationsChanged);
        _contrastSubscribed = TrySubscribe(() => _accessibility.HighContrastChanged += OnHighContrastChanged);
        Refresh(_actualPreview.IsEnabled);
        Present(false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _animation?.Stop();
        _animation = null;
        _observing = false;
        TryUnsubscribe(ref _effectsSubscribed, () => _system.AdvancedEffectsEnabledChanged -= OnEffectsChanged);
        TryUnsubscribe(ref _animationsSubscribed, () => _system.AnimationsEnabledChanged -= OnAnimationsChanged);
        TryUnsubscribe(ref _contrastSubscribed, () => _accessibility.HighContrastChanged -= OnHighContrastChanged);
    }

    private SystemAppearance ReadSystemAppearance()
    {
        var highContrast = true;
        var effects = false;
        var animations = false;
        try { highContrast = _accessibility.HighContrast; } catch (COMException) { }
        try { effects = !highContrast && _system.AdvancedEffectsEnabled; } catch (COMException) { }
        try { animations = !highContrast && _system.AnimationsEnabled; } catch (COMException) { }
        return new(highContrast, effects, animations);
    }

    private static bool TrySubscribe(Action subscribe)
    {
        try { subscribe(); return true; }
        catch (COMException) { return false; }
    }

    private static void TryUnsubscribe(ref bool subscribed, Action unsubscribe)
    {
        if (!subscribed) return;
        subscribed = false;
        try { unsubscribe(); } catch (COMException) { }
    }

    private void OnEffectsChanged(UISettings sender, object args) => QueueRefresh(systemChanged: true);
    private void OnAnimationsChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args) => QueueRefresh(systemChanged: true);
    private void OnHighContrastChanged(AccessibilitySettings sender, object args) => QueueRefresh(systemChanged: true);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        OnUnloaded(this, new RoutedEventArgs());
        _store.Changed -= OnSettingsChanged;
        _actualPreview.Click -= OnPreview;
        _screen.SizeChanged -= OnScreenSizeChanged;
        _layout.SizeChanged -= OnLayoutSizeChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
    }
}
