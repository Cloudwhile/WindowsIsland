using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class NotificationPanel : Grid
{
    private readonly MessageAvatar _icon = new();
    private readonly Grid _text = new();
    private readonly NotificationHeader _header = new();
    private readonly TextBlock _title = IslandTheme.Text("");
    private readonly TextBlock _body = IslandTheme.Text("");
    private readonly PowerInteriorVisuals _powerInterior = new();
    private readonly Visual _headerVisual, _titleVisual, _bodyVisual;
    public FrameworkElement Badge => _icon;

    public NotificationPanel()
    {
        VerticalAlignment = VerticalAlignment.Top;
        HorizontalAlignment = HorizontalAlignment.Center;
        UseLayoutRounding = true;
        ColumnSpacing = NotificationLayout.IconSpacing;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(AppIcon.IconSize) });
        ColumnDefinitions.Add(new ColumnDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _text.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _text.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _text.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _text.VerticalAlignment = VerticalAlignment.Top;

        _title.FontWeight = FontWeights.SemiBold;
        _title.LineHeight = NotificationLayout.TitleHeight;
        _title.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _title.MaxLines = 1;
        _body.LineHeight = 20;
        _body.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _body.MaxLines = 6;
        _body.TextWrapping = TextWrapping.Wrap;
        _text.Margin = _icon.Margin = new Thickness(0, NotificationLayout.HeaderSpacing, 0, 0);

        Grid.SetRow(_body, 1);
        Grid.SetRow(_powerInterior.RailLayer, 2);
        _text.Children.Add(_title);
        _text.Children.Add(_body);
        _text.Children.Add(_powerInterior.RailLayer);

        Grid.SetColumnSpan(_powerInterior.SweepLayer, 2);
        Grid.SetRowSpan(_powerInterior.SweepLayer, 2);
        Grid.SetColumn(_text, 1);
        Grid.SetRow(_icon, 1);
        Grid.SetRow(_text, 1);
        Grid.SetColumnSpan(_header, 2);

        Children.Add(_powerInterior.SweepLayer);
        Children.Add(_header);
        Children.Add(_icon);
        Children.Add(_text);

        _headerVisual = EnableTranslation(_header);
        _titleVisual = EnableTranslation(_title);
        _bodyVisual = EnableTranslation(_body);

        ActualThemeChanged += (_, _) =>
        {
            if (_powerInterior.IsActive)
                _powerInterior.ApplyPalette(ActualTheme == ElementTheme.Light, false);
        };

        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        AutomationProperties.SetAutomationId(_title, "NotificationTitle");
        AutomationProperties.SetAutomationId(_body, "NotificationBody");
    }

    private static Visual EnableTranslation(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        return visual;
    }

    public void Show(IslandNotification notification)
    {
        StopTextMotion();
        _header.Show(notification);
        _title.Text = notification.Title;
        _title.Visibility = string.IsNullOrWhiteSpace(notification.Title) ? Visibility.Collapsed : Visibility.Visible;
        _body.Text = notification.Body;
        _body.Visibility = string.IsNullOrWhiteSpace(notification.Body) ? Visibility.Collapsed : Visibility.Visible;
        _body.Margin = new Thickness(0, _title.Visibility == Visibility.Visible ? NotificationLayout.BodySpacing : 0, 0, 0);
        _icon.Show(notification);
        _powerInterior.Configure(notification, text => _body.Text = text);
        if (_powerInterior.IsActive)
            _powerInterior.ApplyPalette(ActualTheme == ElementTheme.Light, false);
        AutomationProperties.SetName(this, $"{notification.AppName}，{notification.Title}，{notification.Body}");
        FrameworkElementAutomationPeer.FromElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    public void PlayPowerMotion(IslandMotionKind kind, int durationMs, bool lightTheme, bool highContrast, bool opacityTransitions)
    {
        StopTextMotion();
        _icon.PlayPowerMotion(kind, durationMs);
        _powerInterior.Play(kind, durationMs, lightTheme, highContrast);
        if (highContrast || kind == IslandMotionKind.Default) return;

        var compositor = _headerVisual.Compositor;
        using var spring = compositor.CreateCubicBezierEasingFunction(new Vector2(0.18f, 0.90f), new Vector2(0.20f, 1f));
        using var linear = compositor.CreateLinearEasingFunction();

        if (kind == IslandMotionKind.PowerEngage)
        {
            // Staggered left-to-right kinetic cascade out of the battery badge
            AnimateOffset(_headerVisual, compositor, spring, 560,
                (0.00f, new Vector3(-12f, -5f, 0)), (0.55f, new Vector3(2.0f, 0.8f, 0)), (1f, Vector3.Zero));
            AnimateOffset(_titleVisual, compositor, spring, 720,
                (0.00f, new Vector3(-26f, 0, 0)), (0.10f, new Vector3(-26f, 0, 0)),
                (0.56f, new Vector3(5.5f, 0, 0)), (0.80f, new Vector3(-1.4f, 0, 0)), (1f, Vector3.Zero));
            AnimateOffset(_bodyVisual, compositor, spring, 820,
                (0.00f, new Vector3(-18f, 9f, 0)), (0.16f, new Vector3(-18f, 9f, 0)),
                (0.60f, new Vector3(3.2f, -1.6f, 0)), (1f, Vector3.Zero));

            if (opacityTransitions)
            {
                AnimateFade(_titleVisual, compositor, linear, 540, (0f, 0.15f), (0.12f, 0.15f), (0.55f, 1f), (1f, 1f));
                AnimateFade(_bodyVisual, compositor, linear, 640, (0f, 0.0f), (0.18f, 0.0f), (0.65f, 1f), (1f, 1f));
            }
        }
        else if (kind == IslandMotionKind.PowerPromote)
        {
            // 原位状态升级（如「已连接电源 → 开始充电」）：页头与电量正文保持稳固，仅主标题轻柔上翻过渡。
            AnimateOffset(_titleVisual, compositor, spring, 360,
                (0.00f, new Vector3(0, 7f, 0)), (0.68f, new Vector3(0, -0.8f, 0)), (1f, Vector3.Zero));
            if (opacityTransitions)
                AnimateFade(_titleVisual, compositor, linear, 300, (0f, 0.25f), (1f, 1f));
        }
        else if (kind is IslandMotionKind.PowerRelease or IslandMotionKind.PowerAlert)
        {
            // Unplug cord-tug left toward battery, then snap-loose lateral recoil
            AnimateOffset(_headerVisual, compositor, spring, 600,
                (0.00f, new Vector3(5f, 0, 0)), (0.30f, new Vector3(-5f, 0, 0)),
                (0.62f, new Vector3(2.5f, 0, 0)), (1f, Vector3.Zero));
            AnimateOffset(_titleVisual, compositor, spring, 720,
                (0.00f, new Vector3(0, 0, 0)), (0.24f, new Vector3(-10f, 0, 0)),
                (0.52f, new Vector3(9.5f, 0, 0)), (0.76f, new Vector3(-2.8f, 0, 0)), (1f, Vector3.Zero));
            AnimateOffset(_bodyVisual, compositor, spring, 780,
                (0.00f, new Vector3(0, -3f, 0)), (0.28f, new Vector3(-7f, 4f, 0)),
                (0.56f, new Vector3(6.5f, 2f, 0)), (0.80f, new Vector3(-1.8f, 0, 0)), (1f, Vector3.Zero));
        }
    }

    public void StopPowerMotion()
    {
        StopTextMotion();
        _icon.StopPowerMotion();
        _powerInterior.Stop();
    }

    private void StopTextMotion()
    {
        foreach (var visual in new[] { _headerVisual, _titleVisual, _bodyVisual })
        {
            visual.StopAnimation("Translation");
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            visual.Opacity = 1f;
        }
    }

    private static void AnimateOffset(Visual visual, Compositor compositor, CompositionEasingFunction easing,
        int durationMs, params (float At, Vector3 Value)[] frames)
    {
        using var anim = compositor.CreateVector3KeyFrameAnimation();
        anim.Duration = TimeSpan.FromMilliseconds(durationMs);
        foreach (var (at, value) in frames) anim.InsertKeyFrame(at, value, easing);
        visual.StartAnimation("Translation", anim);
    }

    private static void AnimateFade(Visual visual, Compositor compositor, CompositionEasingFunction easing,
        int durationMs, params (float At, float Value)[] frames)
    {
        using var anim = compositor.CreateScalarKeyFrameAnimation();
        anim.Duration = TimeSpan.FromMilliseconds(durationMs);
        foreach (var (at, value) in frames) anim.InsertKeyFrame(at, value, easing);
        visual.StartAnimation(nameof(Visual.Opacity), anim);
    }

    public Size MeasureWindow(double availableWidth, double availableHeight)
    {
        var natural = new Size(double.PositiveInfinity, double.PositiveInfinity);
        _body.MaxLines = 6;
        _title.Measure(natural);
        _body.Measure(natural);
        var contentWidth = Math.Max(_title.DesiredSize.Width, _body.DesiredSize.Width);
        var headerWidth = _header.NaturalWidth() - AppIcon.IconSize - NotificationLayout.IconSpacing;
        var width = NotificationLayout.Width(Math.Max(contentWidth, headerWidth) + 4, availableWidth);
        var textSpace = new Size(Math.Max(1, width - NotificationLayout.TextInset), double.PositiveInfinity);
        var titleHeight = _title.Visibility == Visibility.Visible ? NotificationLayout.TitleHeight : 0;
        var railSpace = _powerInterior.IsActive ? PowerInteriorVisuals.RailRowExtraHeight : 0;
        var bodySpace = Math.Min(NotificationLayout.MaxHeight, availableHeight) - NotificationLayout.VerticalPadding * 2
            - NotificationLayout.HeaderHeight - NotificationLayout.HeaderSpacing - titleHeight - _body.Margin.Top - railSpace;
        _body.MaxLines = Math.Clamp((int)Math.Floor(bodySpace / _body.LineHeight), 1, 6);
        _body.Measure(textSpace);
        var textHeight = titleHeight + _body.DesiredSize.Height
            + (_body.Visibility == Visibility.Visible ? _body.Margin.Top : 0) + railSpace;
        return new Size(width, NotificationLayout.Height(textHeight, availableHeight));
    }

    public void Clear()
    {
        StopTextMotion();
        _powerInterior.Clear();
        _header.Clear();
        _title.Text = _body.Text = "";
        _icon.Clear();
        AutomationProperties.SetName(this, "");
    }
}
