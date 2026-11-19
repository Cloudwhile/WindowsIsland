using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;
using WindowsIsland.Services;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace WindowsIsland.Components;

// 手绘矢量透视能量舱电池图标：支持 40×40 主徽章（磁吸触头、真实电量液面弯月面、刻度与状态徽记）与 16×16 页头微缩矢量。
internal sealed class PowerBatteryIcon : Grid
{
    private readonly bool _compact;
    private PowerBatteryVisual _visualState = new(PowerGlyphMode.Charging, 75, false);
    private bool _hasState;

    // 40×40 Hero elements & reusable brushes (zero per-popup Brush allocations, no outer rectangular tile box)
    private readonly Canvas? _couplerGroup, _liquidGroup, _emblemGroup;
    private readonly XamlPath? _couplerCap, _unplugTicks, _liquidFill, _meniscusLine, _shellTicks, _emblemHalo, _emblemCore;
    private readonly Rectangle? _cordStem, _contactSpark, _terminalNib, _chamberBackdrop, _shellBody, _scanWave;
    private readonly Line? _glassHighlight;
    private Visual? _couplerVisual, _liquidVisual, _emblemVisual, _sparkVisual, _scanWaveVisual;
    private readonly GradientStop _liquidStop0 = new() { Offset = 0 }, _liquidStop1 = new() { Offset = 1 };
    private readonly SolidColorBrush _chamberBrush = new(), _shellBrush = new(), _couplerFillBrush = new();
    private readonly SolidColorBrush _accentBrush = new(), _highlightBrush = new(), _glassBrush = new(), _tickBrush = new();
    private readonly SolidColorBrush _emblemFillBrush = new(), _emblemHaloBrush = new();

    // 16×16 Compact elements
    private readonly Rectangle? _miniBody, _miniCap, _miniFill;
    private readonly XamlPath? _miniBolt;

    public PowerBatteryIcon(double size = AppIcon.IconSize, string automationId = "NotificationAppIcon")
    {
        Width = Height = size;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
        _compact = size <= 20;
        AutomationProperties.SetAutomationId(this, automationId);

        if (_compact)
        {
            var canvas = new Canvas { Width = 16, Height = 16 };
            _miniBody = new Rectangle
            {
                Width = 11.6, Height = 9.0, RadiusX = 2.2, RadiusY = 2.2, StrokeThickness = 1.35,
                Stroke = _shellBrush
            };
            Canvas.SetLeft(_miniBody, 1.0);
            Canvas.SetTop(_miniBody, 3.5);

            _miniCap = new Rectangle { Width = 1.6, Height = 4.2, RadiusX = 0.8, RadiusY = 0.8, Fill = _shellBrush };
            Canvas.SetLeft(_miniCap, 13.3);
            Canvas.SetTop(_miniCap, 5.9);

            _miniFill = new Rectangle { Width = 6.0, Height = 5.4, RadiusX = 1.0, RadiusY = 1.0, Fill = _accentBrush };
            Canvas.SetLeft(_miniFill, 2.8);
            Canvas.SetTop(_miniFill, 5.3);

            _miniBolt = new XamlPath
            {
                Data = BuildPolygon(
                    new(8.2, 2.3), new(4.9, 8.2), new(7.2, 8.2),
                    new(6.1, 13.7), new(9.5, 7.6), new(7.2, 7.6)),
                StrokeThickness = 0.8,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = _emblemFillBrush,
                Stroke = _emblemHaloBrush
            };

            canvas.Children.Add(_miniFill);
            canvas.Children.Add(_miniBody);
            canvas.Children.Add(_miniCap);
            canvas.Children.Add(_miniBolt);
            Children.Add(new Viewbox { Width = size, Height = size, Child = canvas });
        }
        else
        {
            var canvas = new Canvas { Width = 40, Height = 40 };

            // 1. Top magnetic charger coupler
            _couplerGroup = new Canvas { Width = 40, Height = 12 };
            _cordStem = new Rectangle { Width = 2.0, Height = 2.8, RadiusX = 0.9, RadiusY = 0.9, Fill = _shellBrush };
            Canvas.SetLeft(_cordStem, 19.0);
            Canvas.SetTop(_cordStem, 1.2);

            _couplerCap = new XamlPath
            {
                Data = BuildPolygon(
                    new(15.2, 3.6), new(24.8, 3.6), new(25.8, 6.2), new(14.2, 6.2)),
                StrokeThickness = 1.1,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = _couplerFillBrush,
                Stroke = _shellBrush
            };

            _unplugTicks = new XamlPath
            {
                Data = BuildSegments(
                    (new(11.8, 5.0), new(13.5, 6.3)),
                    (new(28.2, 5.0), new(26.5, 6.3))),
                StrokeThickness = 1.25,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stroke = _accentBrush
            };

            _contactSpark = new Rectangle { Width = 9.2, Height = 1.3, RadiusX = 0.65, RadiusY = 0.65, Fill = _highlightBrush };
            Canvas.SetLeft(_contactSpark, 15.4);
            Canvas.SetTop(_contactSpark, 6.8);

            _couplerGroup.Children.Add(_cordStem);
            _couplerGroup.Children.Add(_couplerCap);
            _couplerGroup.Children.Add(_unplugTicks);
            _couplerGroup.Children.Add(_contactSpark);

            // 2. Positive battery terminal nib
            _terminalNib = new Rectangle { Width = 6.8, Height = 2.1, RadiusX = 1.0, RadiusY = 1.0, Fill = _shellBrush };
            Canvas.SetLeft(_terminalNib, 16.6);
            Canvas.SetTop(_terminalNib, 9.4);

            // 3. Clipped inner liquid chamber (16.0 x 20.8 at (12.0, 13.1))
            var cavityHost = new Canvas
            {
                Width = 16.0,
                Height = 20.8,
                Clip = new RectangleGeometry { Rect = new Rect(0, 0, 16.0, 20.8) }
            };
            Canvas.SetLeft(cavityHost, 12.0);
            Canvas.SetTop(cavityHost, 13.1);

            _liquidGroup = new Canvas { Width = 16.0, Height = 30.0 };
            _liquidFill = new XamlPath
            {
                Fill = new LinearGradientBrush
                {
                    StartPoint = new Point(0.5, 0),
                    EndPoint = new Point(0.5, 1),
                    GradientStops = { _liquidStop0, _liquidStop1 }
                }
            };
            _scanWave = new Rectangle
            {
                Width = 14.0,
                Height = 2.2,
                RadiusX = 1.1,
                RadiusY = 1.1,
                Fill = _highlightBrush
            };
            Canvas.SetLeft(_scanWave, 1.0);
            Canvas.SetTop(_scanWave, 18.0);

            _meniscusLine = new XamlPath
            {
                StrokeThickness = 1.15,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stroke = _highlightBrush
            };
            _liquidGroup.Children.Add(_liquidFill);
            _liquidGroup.Children.Add(_scanWave);
            _liquidGroup.Children.Add(_meniscusLine);
            cavityHost.Children.Add(_liquidGroup);

            // 4. Precision outer glass-metal shell, subtle capsule glass tint, specular sheen & calibration ticks
            _chamberBackdrop = new Rectangle
            {
                Width = 19.6, Height = 24.4, RadiusX = 5.2, RadiusY = 5.2,
                Fill = _chamberBrush
            };
            Canvas.SetLeft(_chamberBackdrop, 10.2);
            Canvas.SetTop(_chamberBackdrop, 11.3);

            _shellBody = new Rectangle
            {
                Width = 19.6, Height = 24.4, RadiusX = 5.2, RadiusY = 5.2, StrokeThickness = 1.75,
                Stroke = _shellBrush
            };
            Canvas.SetLeft(_shellBody, 10.2);
            Canvas.SetTop(_shellBody, 11.3);

            _glassHighlight = new Line
            {
                X1 = 13.1, Y1 = 15.2, X2 = 13.1, Y2 = 31.4,
                StrokeThickness = 1.05,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stroke = _glassBrush
            };

            _shellTicks = new XamlPath
            {
                Data = BuildSegments(
                    (new(25.3, 18.2), new(27.5, 18.2)),
                    (new(24.7, 23.5), new(27.5, 23.5)),
                    (new(25.3, 28.8), new(27.5, 28.8))),
                StrokeThickness = 1.05,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stroke = _tickBrush
            };

            // 5. Center hand-drawn state emblem (contrast halo + crisp core)
            _emblemGroup = new Canvas { Width = 40, Height = 40 };
            _emblemHalo = new XamlPath
            {
                StrokeThickness = 2.4,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stroke = _emblemHaloBrush
            };
            _emblemCore = new XamlPath
            {
                StrokeThickness = 1.35,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            _emblemGroup.Children.Add(_emblemHalo);
            _emblemGroup.Children.Add(_emblemCore);

            canvas.Children.Add(_chamberBackdrop);
            canvas.Children.Add(cavityHost);
            canvas.Children.Add(_glassHighlight);
            canvas.Children.Add(_shellTicks);
            canvas.Children.Add(_terminalNib);
            canvas.Children.Add(_shellBody);
            canvas.Children.Add(_couplerGroup);
            canvas.Children.Add(_emblemGroup);

            Children.Add(new Viewbox { Width = size, Height = size, Child = canvas });

            _couplerVisual = ElementCompositionPreview.GetElementVisual(_couplerGroup);
            _liquidVisual = ElementCompositionPreview.GetElementVisual(_liquidGroup);
            _emblemVisual = ElementCompositionPreview.GetElementVisual(_emblemGroup);
            _sparkVisual = ElementCompositionPreview.GetElementVisual(_contactSpark);
            _scanWaveVisual = ElementCompositionPreview.GetElementVisual(_scanWave);
            ElementCompositionPreview.SetIsTranslationEnabled(_couplerGroup, true);
            ElementCompositionPreview.SetIsTranslationEnabled(_liquidGroup, true);
            ElementCompositionPreview.SetIsTranslationEnabled(_emblemGroup, true);
            ElementCompositionPreview.SetIsTranslationEnabled(_scanWave, true);
            _couplerVisual.Properties.InsertVector3("Translation", Vector3.Zero);
            _liquidVisual.Properties.InsertVector3("Translation", Vector3.Zero);
            _emblemVisual.Properties.InsertVector3("Translation", Vector3.Zero);
            _scanWaveVisual.Properties.InsertVector3("Translation", Vector3.Zero);
            _couplerVisual.CenterPoint = new Vector3(20f, 5f, 0f);
            _liquidVisual.CenterPoint = new Vector3(8f, 11f, 0f);
            _emblemVisual.CenterPoint = new Vector3(20f, 23.5f, 0f);
            _sparkVisual.CenterPoint = new Vector3(4.6f, 0.65f, 0f);
            _scanWaveVisual.Opacity = 0f;
        }

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ActualThemeChanged += OnActualThemeChanged;
        if (_hasState) RenderState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ActualThemeChanged -= OnActualThemeChanged;
        StopMotion();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_hasState) RenderState();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new BatteryAutomationPeer(this);

    public void SetAutomationId(string id) => AutomationProperties.SetAutomationId(this, id);

    public void Show(string label, PowerBatteryVisual state)
    {
        _visualState = state;
        _hasState = true;
        AutomationProperties.SetName(this, label);
        RenderState();
    }

    public void PlayMotion(IslandMotionKind kind, int durationMs)
    {
        if (_compact || !_hasState || _couplerVisual is null || _liquidVisual is null || _emblemVisual is null
            || _sparkVisual is null || _scanWaveVisual is null || IsHighContrast()) return;
        StopMotion();
        if (kind == IslandMotionKind.Default) return;
        var compositor = _couplerVisual.Compositor;
        using var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.18f, 0.88f), new Vector2(0.22f, 1f));
        using var linear = compositor.CreateLinearEasingFunction();
        var waveMs = Math.Max(durationMs, 2000);
        if (kind == IslandMotionKind.PowerEngage)
        {
            // Magnetic coupler snaps onto terminal + contact spark flares wide
            AnimateKeyframesY(_couplerVisual, compositor, ease, Math.Max(durationMs, 1100),
                (0f, -6.0f), (0.26f, 1.1f), (0.50f, -0.4f), (1f, 0f));
            AnimateScalars(_couplerVisual, nameof(Visual.RotationAngleInDegrees), compositor, ease, 900,
                (0f, -5.0f), (0.35f, 2.5f), (0.68f, -0.8f), (1f, 0f));
            AnimateSparkScaleX(_sparkVisual, compositor, ease, 1200,
                (0f, 0.25f), (0.24f, 1.85f), (0.52f, 0.85f), (0.76f, 1.25f), (1f, 1f));

            // Liquid surges up from deep chamber while sloshing side-to-side
            AnimateKeyframesY(_liquidVisual, compositor, ease, waveMs,
                (0f, 15.0f), (0.34f, -2.6f), (0.58f, 1.6f), (0.80f, -0.65f), (1f, 0f));
            AnimateScalars(_liquidVisual, nameof(Visual.RotationAngleInDegrees), compositor, ease, waveMs,
                (0f, -9.5f), (0.30f, 7.2f), (0.55f, -4.4f), (0.78f, 1.8f), (1f, 0f));

            // Upward charge scan-wave ascending twice through the liquid
            AnimateKeyframesY(_scanWaveVisual, compositor, ease, waveMs,
                (0f, 3.0f), (0.46f, -18.0f), (0.49f, 3.0f), (0.96f, -18.0f), (1f, 0f));
            AnimateScalars(_scanWaveVisual, nameof(Visual.Opacity), compositor, linear, waveMs,
                (0f, 0.0f), (0.08f, 0.92f), (0.44f, 0.0f), (0.56f, 0.82f), (0.94f, 0.0f), (1f, 0.0f));

            // Center bolt / star double heartbeat
            AnimateEmblemScale(_emblemVisual, compositor, ease, Math.Max(durationMs, 1750),
                (0f, 0.58f), (0.26f, 1.28f), (0.48f, 0.91f), (0.70f, 1.15f), (1f, 1f));
        }
        else if (kind == IslandMotionKind.PowerPromote)
        {
            // Coupler stays magnetically docked; contact spark ignites and bolt blooms in-place
            AnimateSparkScaleX(_sparkVisual, compositor, ease, 950,
                (0f, 0.55f), (0.28f, 1.75f), (0.62f, 0.92f), (1f, 1f));
            AnimateKeyframesY(_liquidVisual, compositor, ease, 1450,
                (0f, 3.4f), (0.42f, -1.2f), (0.75f, 0.45f), (1f, 0f));
            AnimateScalars(_liquidVisual, nameof(Visual.RotationAngleInDegrees), compositor, ease, 1450,
                (0f, -3.6f), (0.38f, 2.8f), (0.72f, -1.1f), (1f, 0f));
            AnimateKeyframesY(_scanWaveVisual, compositor, ease, 1350,
                (0f, 3.0f), (1f, -18.0f));
            AnimateScalars(_scanWaveVisual, nameof(Visual.Opacity), compositor, linear, 1350,
                (0f, 0.0f), (0.14f, 0.88f), (0.88f, 0.0f), (1f, 0.0f));
            AnimateEmblemScale(_emblemVisual, compositor, ease, 1350,
                (0f, 0.40f), (0.32f, 1.22f), (0.64f, 0.95f), (1f, 1f));
        }
        else if (kind is IslandMotionKind.PowerRelease or IslandMotionKind.PowerAlert)
        {
            // Coupler pops off and tilts loose
            AnimateKeyframesY(_couplerVisual, compositor, ease, Math.Max(durationMs, 1250),
                (0f, 3.4f), (0.26f, -3.0f), (0.54f, 0.8f), (1f, 0f));
            AnimateScalars(_couplerVisual, nameof(Visual.RotationAngleInDegrees), compositor, ease, 1150,
                (0f, 0f), (0.28f, 12.0f), (0.58f, -6.5f), (0.82f, 2.2f), (1f, 0f));

            // Liquid drops and sloshes
            AnimateKeyframesY(_liquidVisual, compositor, ease, waveMs,
                (0f, -5.2f), (0.30f, 6.0f), (0.56f, -2.2f), (0.78f, 0.9f), (1f, 0f));
            AnimateScalars(_liquidVisual, nameof(Visual.RotationAngleInDegrees), compositor, ease, waveMs,
                (0f, 8.5f), (0.32f, -7.8f), (0.60f, 4.2f), (0.82f, -1.5f), (1f, 0f));

            // Downward drain ripple
            AnimateKeyframesY(_scanWaveVisual, compositor, ease, 1450,
                (0f, -16.0f), (1f, 4.0f));
            AnimateScalars(_scanWaveVisual, nameof(Visual.Opacity), compositor, linear, 1450,
                (0f, 0.85f), (0.75f, 0.45f), (1f, 0.0f));

            if (kind == IslandMotionKind.PowerAlert)
            {
                AnimateEmblemScale(_emblemVisual, compositor, ease, Math.Max(durationMs, 1700),
                    (0f, 0.56f), (0.24f, 1.32f), (0.46f, 0.88f), (0.68f, 1.20f), (1f, 1f));
            }
            else
            {
                AnimateKeyframesY(_emblemVisual, compositor, ease, Math.Max(durationMs, 1650),
                    (0f, -1.5f), (0.28f, 3.2f), (0.54f, -0.8f), (0.78f, 2.0f), (1f, 0f));
            }
        }
    }

    public void StopMotion()
    {
        if (_couplerVisual is not null)
        {
            _couplerVisual.StopAnimation("Translation");
            _couplerVisual.StopAnimation(nameof(Visual.RotationAngleInDegrees));
            _couplerVisual.Properties.InsertVector3("Translation", Vector3.Zero);
            _couplerVisual.RotationAngleInDegrees = 0f;
        }
        if (_liquidVisual is not null)
        {
            _liquidVisual.StopAnimation("Translation");
            _liquidVisual.StopAnimation(nameof(Visual.RotationAngleInDegrees));
            _liquidVisual.Properties.InsertVector3("Translation", Vector3.Zero);
            _liquidVisual.RotationAngleInDegrees = 0f;
        }
        if (_emblemVisual is not null)
        {
            _emblemVisual.StopAnimation("Translation");
            _emblemVisual.StopAnimation(nameof(Visual.Scale));
            _emblemVisual.Properties.InsertVector3("Translation", Vector3.Zero);
            _emblemVisual.Scale = Vector3.One;
        }
        if (_sparkVisual is not null)
        {
            _sparkVisual.StopAnimation(nameof(Visual.Scale));
            _sparkVisual.Scale = Vector3.One;
        }
        if (_scanWaveVisual is not null)
        {
            _scanWaveVisual.StopAnimation("Translation");
            _scanWaveVisual.StopAnimation(nameof(Visual.Opacity));
            _scanWaveVisual.Properties.InsertVector3("Translation", Vector3.Zero);
            _scanWaveVisual.Opacity = 0f;
        }
    }

    public void Clear()
    {
        _hasState = false;
        StopMotion();
        AutomationProperties.SetName(this, "");
    }

    private static void AnimateKeyframesY(Visual visual, Compositor compositor, CompositionEasingFunction easing,
        int durationMs, params (float At, float Y)[] frames)
    {
        using var anim = compositor.CreateVector3KeyFrameAnimation();
        anim.Duration = TimeSpan.FromMilliseconds(durationMs);
        foreach (var (at, y) in frames) anim.InsertKeyFrame(at, new Vector3(0, y, 0), easing);
        visual.StartAnimation("Translation", anim);
    }

    private static void AnimateEmblemScale(Visual visual, Compositor compositor, CompositionEasingFunction easing,
        int durationMs, params (float At, float Scale)[] frames)
    {
        using var anim = compositor.CreateVector3KeyFrameAnimation();
        anim.Duration = TimeSpan.FromMilliseconds(durationMs);
        foreach (var (at, s) in frames) anim.InsertKeyFrame(at, new Vector3(s, s, 1f), easing);
        visual.StartAnimation(nameof(Visual.Scale), anim);
    }

    private static void AnimateSparkScaleX(Visual visual, Compositor compositor, CompositionEasingFunction easing,
        int durationMs, params (float At, float ScaleX)[] frames)
    {
        using var anim = compositor.CreateVector3KeyFrameAnimation();
        anim.Duration = TimeSpan.FromMilliseconds(durationMs);
        foreach (var (at, sx) in frames) anim.InsertKeyFrame(at, new Vector3(sx, 1f, 1f), easing);
        visual.StartAnimation(nameof(Visual.Scale), anim);
    }

    private static void AnimateScalars(Visual visual, string property, Compositor compositor,
        CompositionEasingFunction easing, int durationMs, params (float At, float Value)[] frames)
    {
        using var anim = compositor.CreateScalarKeyFrameAnimation();
        anim.Duration = TimeSpan.FromMilliseconds(durationMs);
        foreach (var (at, v) in frames) anim.InsertKeyFrame(at, v, easing);
        visual.StartAnimation(property, anim);
    }

    private void RenderState()
    {
        var light = ActualTheme == ElementTheme.Light;
        var highContrast = IsHighContrast();
        var palette = ResolvePalette(_visualState, light, highContrast);

        _shellBrush.Color = palette.ShellStroke;
        _accentBrush.Color = palette.AccentPrimary;
        _emblemFillBrush.Color = palette.EmblemFill;
        _emblemHaloBrush.Color = palette.EmblemHalo;

        if (_compact)
        {
            RenderCompact();
            return;
        }

        _chamberBrush.Color = highContrast
            ? Color.FromArgb(0, 0, 0, 0)
            : Color.FromArgb(light ? (byte)32 : (byte)44,
                palette.AccentPrimary.R, palette.AccentPrimary.G, palette.AccentPrimary.B);

        var unplugged = _visualState.Mode is PowerGlyphMode.Disconnected or PowerGlyphMode.LowBattery;
        Canvas.SetTop(_couplerGroup!, unplugged ? 0.5 : 2.5);
        _couplerFillBrush.Color = palette.CouplerFill;
        _unplugTicks!.Visibility = unplugged ? Visibility.Visible : Visibility.Collapsed;
        _highlightBrush.Color = palette.AccentHighlight;
        _contactSpark!.Visibility = unplugged ? Visibility.Collapsed : Visibility.Visible;

        _glassBrush.Color = highContrast ? Color.FromArgb(0, 0, 0, 0) : Color.FromArgb(light ? (byte)110 : (byte)58, 255, 255, 255);
        _tickBrush.Color = Color.FromArgb(highContrast ? (byte)200 : (byte)96,
            palette.ShellStroke.R, palette.ShellStroke.G, palette.ShellStroke.B);

        var clamped = Math.Clamp(_visualState.Percent, 0, 100);
        var ratio = _visualState.Mode == PowerGlyphMode.Full ? 1.0 : Math.Clamp(clamped / 100.0, 0.12, 0.96);
        var topY = _visualState.Mode == PowerGlyphMode.Full || clamped >= 99 ? 0.0 : 20.8 * (1.0 - ratio);
        var flatTop = topY <= 0.4;
        _liquidFill!.Data = BuildLiquidGeometry(topY, flatTop, unplugged);
        _liquidStop0.Color = palette.AccentPrimary;
        _liquidStop1.Color = palette.AccentDeep;

        _meniscusLine!.Data = BuildMeniscusCurve(topY, flatTop, unplugged);
        _meniscusLine.Visibility = highContrast ? Visibility.Collapsed : Visibility.Visible;

        ConfigureEmblem(_visualState.Mode);
    }

    private void RenderCompact()
    {
        var unplugged = _visualState.Mode is PowerGlyphMode.Disconnected or PowerGlyphMode.LowBattery;
        var clamped = Math.Clamp(_visualState.Percent, 0, 100);
        var ratio = _visualState.Mode == PowerGlyphMode.Full ? 1.0 : Math.Clamp(clamped / 100.0, 0.18, 1.0);
        Canvas.SetLeft(_miniCap!, unplugged ? 14.0 : 13.2);
        _miniFill!.Width = Math.Max(1.6, 8.0 * ratio);
        _miniBolt!.Visibility = _visualState.Mode == PowerGlyphMode.Charging ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ConfigureEmblem(PowerGlyphMode mode)
    {
        if (mode == PowerGlyphMode.Charging)
        {
            Geometry bolt1 = BuildPolygon(
                new(21.3, 15.6), new(15.7, 23.7), new(19.6, 23.7),
                new(18.5, 31.3), new(24.3, 22.9), new(20.4, 22.9));
            Geometry bolt2 = BuildPolygon(
                new(21.3, 15.6), new(15.7, 23.7), new(19.6, 23.7),
                new(18.5, 31.3), new(24.3, 22.9), new(20.4, 22.9));
            _emblemHalo!.Data = bolt1;
            _emblemHalo.Fill = null;
            _emblemCore!.Data = bolt2;
            _emblemCore.Fill = _emblemFillBrush;
            _emblemCore.Stroke = null;
        }
        else if (mode == PowerGlyphMode.Full)
        {
            Geometry star1 = BuildPolygon(
                new(20.0, 17.6), new(21.3, 22.1), new(25.8, 23.4), new(21.3, 24.7),
                new(20.0, 29.2), new(18.7, 24.7), new(14.2, 23.4), new(18.7, 22.1));
            Geometry star2 = BuildPolygon(
                new(20.0, 17.6), new(21.3, 22.1), new(25.8, 23.4), new(21.3, 24.7),
                new(20.0, 29.2), new(18.7, 24.7), new(14.2, 23.4), new(18.7, 22.1));
            _emblemHalo!.Data = star1;
            _emblemHalo.Fill = null;
            _emblemCore!.Data = star2;
            _emblemCore.Fill = _emblemFillBrush;
            _emblemCore.Stroke = null;
        }
        else if (mode == PowerGlyphMode.ChargeLimit)
        {
            var check1 = BuildOpenPolyline(new(16.6, 23.6), new(19.2, 26.2), new(23.8, 20.6));
            var check2 = BuildOpenPolyline(new(16.6, 23.6), new(19.2, 26.2), new(23.8, 20.6));
            _emblemHalo!.Data = check1;
            _emblemHalo.Fill = null;
            _emblemCore!.Data = check2;
            _emblemCore.Fill = null;
            _emblemCore.Stroke = _emblemFillBrush;
        }
        else if (mode == PowerGlyphMode.LowBattery)
        {
            Geometry BuildAlert()
            {
                var group = new GeometryGroup();
                group.Children.Add(BuildPolygon(
                    new(18.85, 17.4), new(21.15, 17.4), new(20.72, 24.5), new(19.28, 24.5)));
                group.Children.Add(BuildPolygon(
                    new(19.05, 26.4), new(20.95, 26.4), new(20.95, 28.3), new(19.05, 28.3)));
                return group;
            }
            _emblemHalo!.Data = BuildAlert();
            _emblemHalo.Fill = null;
            _emblemCore!.Data = BuildAlert();
            _emblemCore.Fill = _emblemFillBrush;
            _emblemCore.Stroke = null;
        }
        else if (mode == PowerGlyphMode.Connected)
        {
            var hold1 = BuildSegments(
                (new(18.2, 20.4), new(18.2, 23.2)),
                (new(21.8, 20.4), new(21.8, 23.2)),
                (new(16.8, 23.4), new(23.2, 23.4)),
                (new(20.0, 23.4), new(20.0, 27.0)));
            var hold2 = BuildSegments(
                (new(18.2, 20.4), new(18.2, 23.2)),
                (new(21.8, 20.4), new(21.8, 23.2)),
                (new(16.8, 23.4), new(23.2, 23.4)),
                (new(20.0, 23.4), new(20.0, 27.0)));
            _emblemHalo!.Data = hold1;
            _emblemHalo.Fill = null;
            _emblemCore!.Data = hold2;
            _emblemCore.Fill = null;
            _emblemCore.Stroke = _emblemFillBrush;
        }
        else
        {
            var chevron1 = BuildOpenPolyline(new(17.2, 22.0), new(20.0, 24.8), new(22.8, 22.0));
            var chevron2 = BuildOpenPolyline(new(17.2, 22.0), new(20.0, 24.8), new(22.8, 22.0));
            _emblemHalo!.Data = chevron1;
            _emblemHalo.Fill = null;
            _emblemCore!.Data = chevron2;
            _emblemCore.Fill = null;
            _emblemCore.Stroke = _emblemFillBrush;
        }
    }

    private static PathGeometry BuildLiquidGeometry(double topY, bool flat, bool unplugged)
    {
        var geometry = new PathGeometry();
        var figure = new PathFigure { IsClosed = true, IsFilled = true };
        if (flat)
        {
            figure.StartPoint = new Point(0, 0);
            figure.Segments.Add(new LineSegment { Point = new Point(16.0, 0) });
        }
        else
        {
            var crest = unplugged ? 1.15 : -1.35;
            figure.StartPoint = new Point(0, topY + (unplugged ? -0.3 : 0.45));
            figure.Segments.Add(new BezierSegment
            {
                Point1 = new Point(5.2, topY + crest),
                Point2 = new Point(10.8, topY - crest),
                Point3 = new Point(16.0, topY + (unplugged ? 0.35 : -0.35))
            });
        }
        figure.Segments.Add(new LineSegment { Point = new Point(16.0, 26.0) });
        figure.Segments.Add(new LineSegment { Point = new Point(0, 26.0) });
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static PathGeometry BuildMeniscusCurve(double topY, bool flat, bool unplugged)
    {
        var geometry = new PathGeometry();
        var figure = new PathFigure { IsClosed = false, IsFilled = false };
        if (flat)
        {
            figure.StartPoint = new Point(0.8, 0.7);
            figure.Segments.Add(new LineSegment { Point = new Point(15.2, 0.7) });
        }
        else
        {
            var crest = unplugged ? 1.15 : -1.35;
            figure.StartPoint = new Point(0.4, topY + (unplugged ? -0.3 : 0.45));
            figure.Segments.Add(new BezierSegment
            {
                Point1 = new Point(5.2, topY + crest),
                Point2 = new Point(10.8, topY - crest),
                Point3 = new Point(15.6, topY + (unplugged ? 0.35 : -0.35))
            });
        }
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static PathGeometry BuildPolygon(params Point[] points)
    {
        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = points[0], IsClosed = true, IsFilled = true };
        for (var index = 1; index < points.Length; index++)
            figure.Segments.Add(new LineSegment { Point = points[index] });
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static PathGeometry BuildOpenPolyline(params Point[] points)
    {
        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = points[0], IsClosed = false, IsFilled = false };
        for (var index = 1; index < points.Length; index++)
            figure.Segments.Add(new LineSegment { Point = points[index] });
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static PathGeometry BuildSegments(params (Point From, Point To)[] lines)
    {
        var geometry = new PathGeometry();
        foreach (var (from, to) in lines)
        {
            var figure = new PathFigure { StartPoint = from, IsClosed = false, IsFilled = false };
            figure.Segments.Add(new LineSegment { Point = to });
            geometry.Figures.Add(figure);
        }
        return geometry;
    }

    private static bool IsHighContrast()
    {
        try { return new AccessibilitySettings().HighContrast; }
        catch (COMException) { return false; }
    }

    private readonly record struct Palette(
        Color ShellStroke, Color CouplerFill,
        Color AccentPrimary, Color AccentDeep, Color AccentHighlight,
        Color EmblemFill, Color EmblemHalo);

    private static Palette ResolvePalette(PowerBatteryVisual state, bool light, bool highContrast)
    {
        if (highContrast)
        {
            var ui = new UISettings();
            var window = ui.GetColorValue(UIColorType.Background);
            var text = ui.GetColorValue(UIColorType.Foreground);
            var highlight = ui.GetColorValue(UIColorType.Accent);
            return new Palette(text, window, highlight, highlight, text, text, window);
        }

        if (state.Mode is PowerGlyphMode.Disconnected or PowerGlyphMode.LowBattery)
        {
            var low = state.Mode == PowerGlyphMode.LowBattery || (state.HasPercent && state.Percent <= 20);
            if (low)
            {
                return light
                    ? new Palette(
                        Color.FromArgb(255, 136, 28, 46), Color.FromArgb(255, 252, 196, 204),
                        Color.FromArgb(255, 239, 68, 68), Color.FromArgb(255, 185, 28, 28), Color.FromArgb(230, 254, 202, 202),
                        Color.FromArgb(255, 185, 28, 28), Color.FromArgb(235, 255, 248, 248))
                    : new Palette(
                        Color.FromArgb(255, 252, 182, 190), Color.FromArgb(255, 74, 30, 38),
                        Color.FromArgb(255, 248, 113, 113), Color.FromArgb(255, 185, 28, 28), Color.FromArgb(220, 254, 202, 202),
                        Color.FromArgb(255, 255, 245, 245), Color.FromArgb(210, 42, 14, 18));
            }

            return light
                ? new Palette(
                    Color.FromArgb(255, 133, 77, 14), Color.FromArgb(255, 251, 216, 160),
                    Color.FromArgb(255, 245, 158, 11), Color.FromArgb(255, 217, 119, 6), Color.FromArgb(235, 254, 240, 180),
                    Color.FromArgb(255, 255, 252, 242), Color.FromArgb(210, 120, 53, 15))
                : new Palette(
                    Color.FromArgb(255, 253, 220, 162), Color.FromArgb(255, 72, 50, 24),
                    Color.FromArgb(255, 251, 191, 36), Color.FromArgb(255, 217, 119, 6), Color.FromArgb(230, 254, 243, 199),
                    Color.FromArgb(255, 255, 251, 235), Color.FromArgb(215, 38, 24, 10));
        }

        if (state.Mode == PowerGlyphMode.Connected)
        {
            return light
                ? new Palette(
                    Color.FromArgb(255, 15, 88, 118), Color.FromArgb(255, 182, 224, 240),
                    Color.FromArgb(255, 14, 165, 233), Color.FromArgb(255, 2, 132, 199), Color.FromArgb(230, 224, 242, 254),
                    Color.FromArgb(255, 250, 254, 255), Color.FromArgb(210, 12, 74, 110))
                : new Palette(
                    Color.FromArgb(255, 186, 230, 253), Color.FromArgb(255, 30, 66, 84),
                    Color.FromArgb(255, 56, 189, 248), Color.FromArgb(255, 3, 105, 161), Color.FromArgb(225, 224, 242, 254),
                    Color.FromArgb(255, 245, 252, 255), Color.FromArgb(215, 12, 30, 40));
        }

        if (state.Mode == PowerGlyphMode.ChargeLimit)
        {
            return light
                ? new Palette(
                    Color.FromArgb(255, 15, 92, 88), Color.FromArgb(255, 166, 232, 224),
                    Color.FromArgb(255, 13, 148, 136), Color.FromArgb(255, 14, 116, 144), Color.FromArgb(235, 204, 251, 241),
                    Color.FromArgb(255, 250, 255, 254), Color.FromArgb(215, 13, 74, 72))
                : new Palette(
                    Color.FromArgb(255, 180, 244, 238), Color.FromArgb(255, 24, 70, 70),
                    Color.FromArgb(255, 45, 212, 191), Color.FromArgb(255, 8, 145, 178), Color.FromArgb(230, 204, 251, 241),
                    Color.FromArgb(255, 245, 255, 253), Color.FromArgb(220, 9, 28, 29));
        }

        // Charging & Full: Crisp Fluent Emerald-Teal
        return light
            ? new Palette(
                Color.FromArgb(255, 6, 95, 70), Color.FromArgb(255, 167, 232, 206),
                Color.FromArgb(255, 16, 185, 129), Color.FromArgb(255, 5, 150, 105), Color.FromArgb(235, 209, 250, 229),
                Color.FromArgb(255, 252, 255, 253), Color.FromArgb(215, 6, 78, 59))
            : new Palette(
                Color.FromArgb(255, 182, 244, 220), Color.FromArgb(255, 26, 72, 60),
                Color.FromArgb(255, 52, 211, 153), Color.FromArgb(255, 13, 148, 136), Color.FromArgb(230, 209, 250, 229),
                Color.FromArgb(255, 246, 255, 251), Color.FromArgb(220, 9, 28, 23));
    }

    private sealed class BatteryAutomationPeer(PowerBatteryIcon owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetClassNameCore() => nameof(PowerBatteryIcon);
    }
}
