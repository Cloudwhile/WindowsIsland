using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using WindowsIsland.Services;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace WindowsIsland.Components;

// 电源弹窗窗体内部动效层：斜向流光掠影、星芒/菱形/流光粒子场、底部内嵌实时电量导轨及百分比数字滚动。
internal sealed class PowerInteriorVisuals
{
    private const double RailHeight = 5.0;
    private readonly Grid _sweepHost;
    private readonly XamlPath _sweepRibbon;
    private readonly Visual _sweepVisual;
    private readonly PowerParticleField _particles = new();
    private readonly GradientStop _sweepStop0 = new() { Offset = 0.0 };
    private readonly GradientStop _sweepStop1 = new() { Offset = 0.38 };
    private readonly GradientStop _sweepStop2 = new() { Offset = 0.56 };
    private readonly GradientStop _sweepStop3 = new() { Offset = 1.0 };

    private readonly Grid _railHost;
    private readonly Border _railTrack, _railFill, _railSpark;
    private readonly Canvas _railTicks = new() { Height = RailHeight, IsHitTestVisible = false };
    private readonly Line _tick25 = CreateTick(), _tick50 = CreateTick(), _tick75 = CreateTick();
    private readonly Visual _railFillVisual, _railSparkVisual;
    private readonly RectangleGeometry _railClip = new();
    private readonly SolidColorBrush _trackBrush = new(), _tickBrush = new(), _sparkBrush = new();
    private readonly GradientStop _fillStop0 = new() { Offset = 0 }, _fillStop1 = new() { Offset = 1 };

    private DispatcherQueueTimer? _counterTimer;
    private Action<string>? _updateBodyText;
    private string _finalBodyText = "";
    private string? _eventId;
    private PowerBatteryVisual _state;
    private bool _active;
    private double _railWidth = 180, _sweepWidth = 300;

    public FrameworkElement SweepLayer => _sweepHost;
    public FrameworkElement RailLayer => _railHost;
    public bool IsActive => _active;
    public const double RailRowExtraHeight = 12.0;

    public PowerInteriorVisuals()
    {
        // 1. Clipped diagonal specular light ribbon across the capsule interior
        _sweepRibbon = new XamlPath
        {
            Data = BuildRibbonGeometry(),
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5),
                GradientStops = { _sweepStop0, _sweepStop1, _sweepStop2, _sweepStop3 }
            }
        };
        var sweepCanvas = new Canvas { IsHitTestVisible = false };
        sweepCanvas.Children.Add(_sweepRibbon);
        _sweepHost = new Grid
        {
            Margin = new Thickness(-8, -8, -8, -8),
            CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        _sweepHost.Children.Add(sweepCanvas);
        _sweepHost.Children.Add(_particles.Element);
        _sweepHost.SizeChanged += (_, e) =>
        {
            _sweepWidth = Math.Max(120, e.NewSize.Width);
            _particles.SetBounds(e.NewSize.Width, e.NewSize.Height);
        };
        _sweepVisual = ElementCompositionPreview.GetElementVisual(_sweepRibbon);
        ElementCompositionPreview.SetIsTranslationEnabled(_sweepRibbon, true);
        _sweepVisual.Properties.InsertVector3("Translation", Vector3.Zero);
        _sweepVisual.Opacity = 0f;

        // 2. Dedicated bottom live battery capacity rail (groove + animated fill + travelling spark + 25/50/75% ticks)
        _railTrack = new Border
        {
            Height = RailHeight,
            CornerRadius = new CornerRadius(RailHeight * 0.5),
            Background = _trackBrush
        };
        _railFill = new Border
        {
            Height = RailHeight,
            CornerRadius = new CornerRadius(RailHeight * 0.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5),
                GradientStops = { _fillStop0, _fillStop1 }
            }
        };
        _railSpark = new Border
        {
            Width = 22,
            Height = RailHeight,
            CornerRadius = new CornerRadius(RailHeight * 0.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = _sparkBrush
        };
        _tick25.Stroke = _tick50.Stroke = _tick75.Stroke = _tickBrush;
        _railTicks.Children.Add(_tick25);
        _railTicks.Children.Add(_tick50);
        _railTicks.Children.Add(_tick75);

        _railHost = new Grid
        {
            Height = RailHeight,
            Margin = new Thickness(0, 7, 0, 0),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Clip = _railClip
        };
        _railHost.Children.Add(_railTrack);
        _railHost.Children.Add(_railFill);
        _railHost.Children.Add(_railSpark);
        _railHost.Children.Add(_railTicks);
        _railHost.SizeChanged += (_, e) =>
        {
            _railWidth = Math.Max(60, e.NewSize.Width);
            _railClip.Rect = new Rect(0, 0, e.NewSize.Width, Math.Max(RailHeight, e.NewSize.Height));
            LayoutRailGeometry();
        };

        _railFillVisual = ElementCompositionPreview.GetElementVisual(_railFill);
        _railSparkVisual = ElementCompositionPreview.GetElementVisual(_railSpark);
        ElementCompositionPreview.SetIsTranslationEnabled(_railSpark, true);
        _railFillVisual.CenterPoint = new Vector3(0f, (float)(RailHeight * 0.5), 0f);
        _railSparkVisual.Properties.InsertVector3("Translation", Vector3.Zero);
        _railSparkVisual.Opacity = 0f;
    }

    public void Configure(IslandNotification notification, Action<string> setBodyText)
    {
        Stop();
        if (!PowerEvents.TryResolveVisual(notification, out var state))
        {
            _active = false;
            _sweepHost.Visibility = _railHost.Visibility = Visibility.Collapsed;
            return;
        }

        _active = true;
        _state = state;
        _eventId = notification.EventId;
        _finalBodyText = notification.Body;
        _updateBodyText = setBodyText;
        _sweepHost.Visibility = _railHost.Visibility = Visibility.Visible;
        LayoutRailGeometry();
    }

    public void ApplyPalette(bool lightTheme, bool highContrast)
    {
        if (!_active) return;
        if (highContrast)
        {
            _sweepHost.Visibility = Visibility.Collapsed;
            _railSpark.Visibility = Visibility.Collapsed;
            _particles.ApplyPalette(_state, lightTheme, highContrast: true);
            _trackBrush.Color = Color.FromArgb(90, 160, 160, 160);
            _fillStop0.Color = _fillStop1.Color = Color.FromArgb(255, 52, 211, 153);
            _tickBrush.Color = Color.FromArgb(0, 0, 0, 0);
            return;
        }

        _sweepHost.Visibility = Visibility.Visible;
        _railSpark.Visibility = Visibility.Visible;
        _particles.ApplyPalette(_state, lightTheme, highContrast: false);
        _trackBrush.Color = lightTheme
            ? Color.FromArgb(34, 15, 23, 42)
            : Color.FromArgb(56, 255, 255, 255);
        _tickBrush.Color = lightTheme
            ? Color.FromArgb(72, 255, 255, 255)
            : Color.FromArgb(88, 15, 23, 42);

        if (_state.Mode is PowerGlyphMode.Disconnected or PowerGlyphMode.LowBattery)
        {
            var low = _state.Mode == PowerGlyphMode.LowBattery || (_state.HasPercent && _state.Percent <= 20);
            var primary = low
                ? (lightTheme ? Color.FromArgb(255, 220, 38, 38) : Color.FromArgb(255, 248, 113, 113))
                : (lightTheme ? Color.FromArgb(255, 217, 119, 6) : Color.FromArgb(255, 245, 158, 11));
            var bright = low
                ? Color.FromArgb(255, 254, 202, 202)
                : (lightTheme ? Color.FromArgb(255, 245, 158, 11) : Color.FromArgb(255, 253, 224, 71));
            _fillStop0.Color = primary;
            _fillStop1.Color = bright;
            _sparkBrush.Color = low ? Color.FromArgb(245, 255, 241, 242) : Color.FromArgb(245, 255, 251, 235);
            SetSweepColor(bright, lightTheme ? (byte)52 : (byte)64);
        }
        else if (_state.Mode == PowerGlyphMode.ChargeLimit)
        {
            _fillStop0.Color = lightTheme ? Color.FromArgb(255, 13, 148, 136) : Color.FromArgb(255, 20, 184, 166);
            _fillStop1.Color = lightTheme ? Color.FromArgb(255, 6, 182, 212) : Color.FromArgb(255, 45, 212, 191);
            _sparkBrush.Color = Color.FromArgb(250, 240, 253, 250);
            SetSweepColor(_fillStop1.Color, lightTheme ? (byte)56 : (byte)70);
        }
        else if (_state.Mode == PowerGlyphMode.Connected)
        {
            _fillStop0.Color = lightTheme ? Color.FromArgb(255, 2, 132, 199) : Color.FromArgb(255, 14, 165, 233);
            _fillStop1.Color = lightTheme ? Color.FromArgb(255, 56, 189, 248) : Color.FromArgb(255, 125, 211, 252);
            _sparkBrush.Color = Color.FromArgb(245, 240, 249, 255);
            SetSweepColor(_fillStop1.Color, lightTheme ? (byte)50 : (byte)62);
        }
        else
        {
            _fillStop0.Color = lightTheme ? Color.FromArgb(255, 5, 150, 105) : Color.FromArgb(255, 16, 185, 129);
            _fillStop1.Color = lightTheme ? Color.FromArgb(255, 14, 165, 233) : Color.FromArgb(255, 52, 211, 153);
            _sparkBrush.Color = Color.FromArgb(250, 236, 253, 245);
            SetSweepColor(_fillStop1.Color, lightTheme ? (byte)56 : (byte)72);
        }
    }

    public void Play(IslandMotionKind kind, int durationMs, bool lightTheme, bool highContrast)
    {
        if (!_active) return;
        Stop();
        ApplyPalette(lightTheme, highContrast);
        LayoutRailGeometry();
        if (highContrast || kind == IslandMotionKind.Default) return;

        if (_sweepHost.ActualWidth > 0 && _sweepHost.ActualHeight > 0)
            _particles.SetBounds(_sweepHost.ActualWidth, _sweepHost.ActualHeight);
        _particles.Play(kind, durationMs, highContrast);

        var compositor = _sweepVisual.Compositor;
        using var easeOut = compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 0.88f), new Vector2(0.20f, 1f));
        using var linear = compositor.CreateLinearEasingFunction();

        var fillWidth = (float)Math.Max(12.0, _railFill.Width);
        var sweepSpan = (float)Math.Max(260.0, _sweepWidth + 90.0);

        if (kind == IslandMotionKind.PowerEngage)
        {
            // 1. Two-pass diagonal energy sheen sweeping left -> right across the window interior
            using (var sweepAnim = compositor.CreateVector3KeyFrameAnimation())
            {
                sweepAnim.Duration = TimeSpan.FromMilliseconds(durationMs);
                sweepAnim.InsertKeyFrame(0.00f, new Vector3(-30f, 0, 0));
                sweepAnim.InsertKeyFrame(0.46f, new Vector3(sweepSpan, 0, 0), easeOut);
                sweepAnim.InsertKeyFrame(0.50f, new Vector3(-30f, 0, 0), linear);
                sweepAnim.InsertKeyFrame(0.96f, new Vector3(sweepSpan, 0, 0), easeOut);
                _sweepVisual.StartAnimation("Translation", sweepAnim);
            }
            using (var sweepOpacity = compositor.CreateScalarKeyFrameAnimation())
            {
                sweepOpacity.Duration = TimeSpan.FromMilliseconds(durationMs);
                sweepOpacity.InsertKeyFrame(0.00f, 0.0f);
                sweepOpacity.InsertKeyFrame(0.10f, 1.0f, linear);
                sweepOpacity.InsertKeyFrame(0.42f, 0.75f, linear);
                sweepOpacity.InsertKeyFrame(0.48f, 0.0f, linear);
                sweepOpacity.InsertKeyFrame(0.58f, 0.65f, linear);
                sweepOpacity.InsertKeyFrame(0.96f, 0.0f, linear);
                _sweepVisual.StartAnimation(nameof(Visual.Opacity), sweepOpacity);
            }

            // 2. Live capacity rail surging from 0 -> target % with elastic overshoot
            using (var railScale = compositor.CreateVector3KeyFrameAnimation())
            {
                railScale.Duration = TimeSpan.FromMilliseconds(Math.Min(durationMs, 1350));
                railScale.InsertKeyFrame(0.00f, new Vector3(0.06f, 1f, 1f));
                railScale.InsertKeyFrame(0.42f, new Vector3(1.07f, 1f, 1f), easeOut);
                railScale.InsertKeyFrame(0.70f, new Vector3(0.97f, 1f, 1f), easeOut);
                railScale.InsertKeyFrame(1.00f, Vector3.One, easeOut);
                _railFillVisual.StartAnimation(nameof(Visual.Scale), railScale);
            }

            // 3. Bright charge spark racing along the filled rail twice
            using (var sparkMove = compositor.CreateVector3KeyFrameAnimation())
            {
                sparkMove.Duration = TimeSpan.FromMilliseconds(durationMs);
                sparkMove.InsertKeyFrame(0.00f, new Vector3(-18f, 0, 0));
                sparkMove.InsertKeyFrame(0.45f, new Vector3(Math.Max(0f, fillWidth - 16f), 0, 0), easeOut);
                sparkMove.InsertKeyFrame(0.48f, new Vector3(-18f, 0, 0), linear);
                sparkMove.InsertKeyFrame(0.94f, new Vector3(Math.Max(0f, fillWidth - 14f), 0, 0), easeOut);
                _railSparkVisual.StartAnimation("Translation", sparkMove);
            }
            using (var sparkOpacity = compositor.CreateScalarKeyFrameAnimation())
            {
                sparkOpacity.Duration = TimeSpan.FromMilliseconds(durationMs);
                sparkOpacity.InsertKeyFrame(0.00f, 0.95f);
                sparkOpacity.InsertKeyFrame(0.44f, 0.90f, linear);
                sparkOpacity.InsertKeyFrame(0.47f, 0.0f, linear);
                sparkOpacity.InsertKeyFrame(0.54f, 0.85f, linear);
                sparkOpacity.InsertKeyFrame(1.00f, 0.0f, linear);
                _railSparkVisual.StartAnimation(nameof(Visual.Opacity), sparkOpacity);
            }

            StartPercentageRoll(engage: true);
        }
        else if (kind == IslandMotionKind.PowerPromote)
        {
            // 原位充能点火：电量导轨与百分比数字保持平稳不回退重滚，仅流光单次掠过并沿导轨发送充能脉冲。
            using (var sweepAnim = compositor.CreateVector3KeyFrameAnimation())
            {
                sweepAnim.Duration = TimeSpan.FromMilliseconds(Math.Min(durationMs, 1150));
                sweepAnim.InsertKeyFrame(0.00f, new Vector3(-30f, 0, 0));
                sweepAnim.InsertKeyFrame(1.00f, new Vector3(sweepSpan, 0, 0), easeOut);
                _sweepVisual.StartAnimation("Translation", sweepAnim);
            }
            using (var sweepOpacity = compositor.CreateScalarKeyFrameAnimation())
            {
                sweepOpacity.Duration = TimeSpan.FromMilliseconds(Math.Min(durationMs, 1150));
                sweepOpacity.InsertKeyFrame(0.00f, 0.0f);
                sweepOpacity.InsertKeyFrame(0.16f, 0.78f, linear);
                sweepOpacity.InsertKeyFrame(0.68f, 0.45f, linear);
                sweepOpacity.InsertKeyFrame(1.00f, 0.0f, linear);
                _sweepVisual.StartAnimation(nameof(Visual.Opacity), sweepOpacity);
            }

            using (var railScale = compositor.CreateVector3KeyFrameAnimation())
            {
                railScale.Duration = TimeSpan.FromMilliseconds(520);
                railScale.InsertKeyFrame(0.00f, Vector3.One);
                railScale.InsertKeyFrame(0.45f, new Vector3(1.032f, 1f, 1f), easeOut);
                railScale.InsertKeyFrame(1.00f, Vector3.One, easeOut);
                _railFillVisual.StartAnimation(nameof(Visual.Scale), railScale);
            }

            using (var sparkMove = compositor.CreateVector3KeyFrameAnimation())
            {
                sparkMove.Duration = TimeSpan.FromMilliseconds(Math.Min(durationMs, 1100));
                sparkMove.InsertKeyFrame(0.00f, new Vector3(-18f, 0, 0));
                sparkMove.InsertKeyFrame(1.00f, new Vector3(Math.Max(0f, fillWidth - 14f), 0, 0), easeOut);
                _railSparkVisual.StartAnimation("Translation", sparkMove);
            }
            using (var sparkOpacity = compositor.CreateScalarKeyFrameAnimation())
            {
                sparkOpacity.Duration = TimeSpan.FromMilliseconds(Math.Min(durationMs, 1100));
                sparkOpacity.InsertKeyFrame(0.00f, 0.92f);
                sparkOpacity.InsertKeyFrame(0.76f, 0.75f, linear);
                sparkOpacity.InsertKeyFrame(1.00f, 0.0f, linear);
                _railSparkVisual.StartAnimation(nameof(Visual.Opacity), sparkOpacity);
            }
        }
        else if (kind is IslandMotionKind.PowerRelease or IslandMotionKind.PowerAlert)
        {
            // 1. Reverse amber sheen pulling right -> left back toward the battery
            using (var sweepAnim = compositor.CreateVector3KeyFrameAnimation())
            {
                sweepAnim.Duration = TimeSpan.FromMilliseconds(Math.Min(durationMs, 1200));
                sweepAnim.InsertKeyFrame(0.00f, new Vector3(sweepSpan, 0, 0));
                sweepAnim.InsertKeyFrame(1.00f, new Vector3(-40f, 0, 0), easeOut);
                _sweepVisual.StartAnimation("Translation", sweepAnim);
            }
            using (var sweepOpacity = compositor.CreateScalarKeyFrameAnimation())
            {
                sweepOpacity.Duration = TimeSpan.FromMilliseconds(Math.Min(durationMs, 1200));
                sweepOpacity.InsertKeyFrame(0.00f, 0.95f);
                sweepOpacity.InsertKeyFrame(0.65f, 0.55f, linear);
                sweepOpacity.InsertKeyFrame(1.00f, 0.0f, linear);
                _sweepVisual.StartAnimation(nameof(Visual.Opacity), sweepOpacity);
            }

            // 2. Rail recoils from stretched tug down to remaining capacity
            using (var railScale = compositor.CreateVector3KeyFrameAnimation())
            {
                railScale.Duration = TimeSpan.FromMilliseconds(Math.Min(durationMs, 1250));
                railScale.InsertKeyFrame(0.00f, new Vector3(1.12f, 1f, 1f));
                railScale.InsertKeyFrame(0.38f, new Vector3(0.88f, 1f, 1f), easeOut);
                railScale.InsertKeyFrame(0.72f, new Vector3(1.02f, 1f, 1f), easeOut);
                railScale.InsertKeyFrame(1.00f, Vector3.One, easeOut);
                _railFillVisual.StartAnimation(nameof(Visual.Scale), railScale);
            }

            // 3. Amber spark retreating back into the left battery terminal
            using (var sparkMove = compositor.CreateVector3KeyFrameAnimation())
            {
                sparkMove.Duration = TimeSpan.FromMilliseconds(Math.Min(durationMs, 1350));
                sparkMove.InsertKeyFrame(0.00f, new Vector3(Math.Max(0f, fillWidth - 14f), 0, 0));
                sparkMove.InsertKeyFrame(1.00f, new Vector3(-20f, 0, 0), easeOut);
                _railSparkVisual.StartAnimation("Translation", sparkMove);
            }
            using (var sparkOpacity = compositor.CreateScalarKeyFrameAnimation())
            {
                sparkOpacity.Duration = TimeSpan.FromMilliseconds(Math.Min(durationMs, 1350));
                sparkOpacity.InsertKeyFrame(0.00f, 0.95f);
                sparkOpacity.InsertKeyFrame(0.75f, 0.60f, linear);
                sparkOpacity.InsertKeyFrame(1.00f, 0.0f, linear);
                _railSparkVisual.StartAnimation(nameof(Visual.Opacity), sparkOpacity);
            }

            StartPercentageRoll(engage: false);
        }
    }

    public void Stop()
    {
        StopCounter();
        _particles.Stop();
        _sweepVisual.StopAnimation("Translation");
        _sweepVisual.StopAnimation(nameof(Visual.Opacity));
        _sweepVisual.Opacity = 0f;
        _sweepVisual.Properties.InsertVector3("Translation", Vector3.Zero);

        _railFillVisual.StopAnimation(nameof(Visual.Scale));
        _railFillVisual.Scale = Vector3.One;

        _railSparkVisual.StopAnimation("Translation");
        _railSparkVisual.StopAnimation(nameof(Visual.Opacity));
        _railSparkVisual.Opacity = 0f;
        _railSparkVisual.Properties.InsertVector3("Translation", Vector3.Zero);
    }

    public void Clear()
    {
        Stop();
        _active = false;
        _updateBodyText = null;
        _eventId = null;
        _finalBodyText = "";
        _sweepHost.Visibility = _railHost.Visibility = Visibility.Collapsed;
    }

    private void StartPercentageRoll(bool engage)
    {
        if (!_state.HasPercent || _updateBodyText is null || _railHost.DispatcherQueue is not { } queue) return;
        var target = Math.Clamp(_state.Percent, 0, 100);
        var start = engage ? Math.Max(0, target - Math.Max(12, Math.Min(32, target / 2))) : Math.Min(100, target + 4);
        if (start == target) return;

        const int totalSteps = 16;
        var step = 0;
        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(36);
        _counterTimer = timer;
        _updateBodyText(PowerEvents.FormatBody(_eventId, start));
        timer.Tick += (_, _) =>
        {
            if (!_active || !ReferenceEquals(_counterTimer, timer))
            {
                timer.Stop();
                return;
            }
            step++;
            if (step >= totalSteps)
            {
                timer.Stop();
                _counterTimer = null;
                _updateBodyText?.Invoke(_finalBodyText);
                return;
            }
            var t = 1.0 - Math.Pow(1.0 - (double)step / totalSteps, 2.6);
            var current = (int)Math.Round(start + (target - start) * t);
            _updateBodyText?.Invoke(PowerEvents.FormatBody(_eventId, current));
        };
        timer.Start();
    }

    private void StopCounter()
    {
        if (_counterTimer is not null)
        {
            _counterTimer.Stop();
            _counterTimer = null;
            if (_active && _finalBodyText.Length > 0) _updateBodyText?.Invoke(_finalBodyText);
        }
    }

    private void LayoutRailGeometry()
    {
        if (!_active) return;
        var clamped = Math.Clamp(_state.Percent, 0, 100);
        var ratio = _state.Mode == PowerGlyphMode.Full ? 1.0 : Math.Clamp(clamped / 100.0, 0.08, 1.0);
        _railFill.Width = Math.Max(8.0, _railWidth * ratio);
        SetTickX(_tick25, _railWidth * 0.25);
        SetTickX(_tick50, _railWidth * 0.50);
        SetTickX(_tick75, _railWidth * 0.75);
    }

    private void SetSweepColor(Color tint, byte peakAlpha)
    {
        _sweepStop0.Color = Color.FromArgb(0, tint.R, tint.G, tint.B);
        _sweepStop1.Color = Color.FromArgb((byte)(peakAlpha * 0.55), tint.R, tint.G, tint.B);
        _sweepStop2.Color = Color.FromArgb(peakAlpha, 255, 255, 255);
        _sweepStop3.Color = Color.FromArgb(0, tint.R, tint.G, tint.B);
    }

    private static Line CreateTick() => new()
    {
        Y1 = 0, Y2 = RailHeight, StrokeThickness = 1.0
    };

    private static void SetTickX(Line line, double x) => line.X1 = line.X2 = Math.Round(x);

    private static PathGeometry BuildRibbonGeometry()
    {
        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = new Point(-56, -8), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment { Point = new Point(22, -8) });
        figure.Segments.Add(new LineSegment { Point = new Point(-18, 168) });
        figure.Segments.Add(new LineSegment { Point = new Point(-96, 168) });
        geometry.Figures.Add(figure);
        return geometry;
    }
}
