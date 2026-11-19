using System.Numerics;
using Microsoft.UI.Composition;
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

// 电源弹窗窗体内部粒子场：基于 GPU Composition 驱动的星芒微粒、菱形离子与定向流光短线（无球体/无虚化云团，零逐帧计时器）。
internal sealed class PowerParticleField
{
    private enum ParticleShapeKind
    {
        StarGlint,
        DiamondMote,
        IonStreak
    }

    private readonly record struct ParticleSpec(
        ParticleShapeKind Kind,
        float SizeX,
        float SizeY,
        bool Highlight,
        float SeedYRatio,
        float SpreadXRatio,
        float DriftY,
        float Delay,
        float Span,
        float PeakOpacity,
        float SpinDegrees);

    private sealed record ParticleNode(Visual Visual, ParticleSpec Spec);

    private static readonly ParticleSpec[] Specs =
    [
        // Wave 1: Immediate coupler-snap burst (0.00 .. 0.56)
        new(ParticleShapeKind.StarGlint,   8.0f, 8.0f, true,  0.34f, 0.42f, -14f, 0.01f, 0.48f, 0.92f,  95f),
        new(ParticleShapeKind.IonStreak,  14.0f, 2.2f, true,  0.48f, 0.64f,  -4f, 0.02f, 0.44f, 0.86f,   0f),
        new(ParticleShapeKind.DiamondMote, 5.4f, 5.4f, false, 0.66f, 0.52f,  11f, 0.04f, 0.50f, 0.84f, 140f),
        new(ParticleShapeKind.StarGlint,   6.8f, 6.8f, false, 0.24f, 0.72f, -16f, 0.07f, 0.52f, 0.82f, -110f),
        new(ParticleShapeKind.IonStreak,  11.5f, 2.0f, false, 0.74f, 0.78f,   6f, 0.09f, 0.46f, 0.78f,   0f),
        new(ParticleShapeKind.DiamondMote, 4.8f, 4.8f, true,  0.42f, 0.86f, -10f, 0.12f, 0.52f, 0.85f, 165f),
        new(ParticleShapeKind.StarGlint,   7.4f, 7.4f, true,  0.58f, 0.60f,  13f, 0.15f, 0.50f, 0.88f,  85f),

        // Wave 2: Sustained effervescent energy stream (0.30 .. 0.98)
        new(ParticleShapeKind.IonStreak,  15.0f, 2.2f, true,  0.32f, 0.88f,  -6f, 0.30f, 0.50f, 0.84f,   0f),
        new(ParticleShapeKind.StarGlint,   7.6f, 7.6f, false, 0.52f, 0.74f, -15f, 0.34f, 0.52f, 0.86f, 120f),
        new(ParticleShapeKind.DiamondMote, 5.2f, 5.2f, true,  0.28f, 0.56f, -12f, 0.38f, 0.50f, 0.80f, -150f),
        new(ParticleShapeKind.IonStreak,  12.0f, 2.0f, false, 0.68f, 0.84f,   8f, 0.42f, 0.48f, 0.76f,   0f),
        new(ParticleShapeKind.StarGlint,   8.2f, 8.2f, true,  0.44f, 0.92f, -11f, 0.45f, 0.50f, 0.90f, -95f),
        new(ParticleShapeKind.DiamondMote, 4.6f, 4.6f, false, 0.76f, 0.66f,  10f, 0.48f, 0.48f, 0.78f, 135f),
        new(ParticleShapeKind.StarGlint,   6.6f, 6.6f, false, 0.36f, 0.80f, -13f, 0.52f, 0.46f, 0.82f, 110f)
    ];

    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private readonly ParticleNode[] _nodes;
    private readonly SolidColorBrush _primaryBrush = new(), _highlightBrush = new();
    private double _width = 300, _height = 96;

    public FrameworkElement Element => _canvas;

    public PowerParticleField()
    {
        _nodes = new ParticleNode[Specs.Length];
        for (var i = 0; i < Specs.Length; i++)
        {
            var spec = Specs[i];
            var brush = spec.Highlight ? _highlightBrush : _primaryBrush;
            var shape = CreateShape(spec, brush);
            _canvas.Children.Add(shape);

            var visual = ElementCompositionPreview.GetElementVisual(shape);
            ElementCompositionPreview.SetIsTranslationEnabled(shape, true);
            visual.CenterPoint = new Vector3(spec.SizeX * 0.5f, spec.SizeY * 0.5f, 0f);
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            visual.Opacity = 0f;
            _nodes[i] = new ParticleNode(visual, spec);
        }
    }

    public void SetBounds(double width, double height)
    {
        _width = Math.Max(140, width);
        _height = Math.Max(68, height);
    }

    public void ApplyPalette(PowerBatteryVisual state, bool lightTheme, bool highContrast)
    {
        if (highContrast)
        {
            _canvas.Visibility = Visibility.Collapsed;
            return;
        }

        _canvas.Visibility = Visibility.Visible;
        if (state.Mode is PowerGlyphMode.Disconnected or PowerGlyphMode.LowBattery)
        {
            var low = state.Mode == PowerGlyphMode.LowBattery || (state.HasPercent && state.Percent <= 20);
            if (low)
            {
                _primaryBrush.Color = lightTheme
                    ? Color.FromArgb(220, 225, 29, 72)
                    : Color.FromArgb(240, 251, 113, 133);
                _highlightBrush.Color = lightTheme
                    ? Color.FromArgb(230, 185, 28, 28)
                    : Color.FromArgb(250, 254, 205, 211);
            }
            else
            {
                _primaryBrush.Color = lightTheme
                    ? Color.FromArgb(215, 217, 119, 6)
                    : Color.FromArgb(238, 251, 191, 36);
                _highlightBrush.Color = lightTheme
                    ? Color.FromArgb(225, 180, 83, 9)
                    : Color.FromArgb(248, 254, 243, 199);
            }
        }
        else if (state.Mode == PowerGlyphMode.ChargeLimit)
        {
            _primaryBrush.Color = lightTheme
                ? Color.FromArgb(215, 13, 148, 136)
                : Color.FromArgb(238, 45, 212, 191);
            _highlightBrush.Color = lightTheme
                ? Color.FromArgb(225, 8, 145, 178)
                : Color.FromArgb(248, 204, 251, 241);
        }
        else if (state.Mode == PowerGlyphMode.Connected)
        {
            _primaryBrush.Color = lightTheme
                ? Color.FromArgb(215, 2, 132, 199)
                : Color.FromArgb(238, 56, 189, 248);
            _highlightBrush.Color = lightTheme
                ? Color.FromArgb(225, 3, 105, 161)
                : Color.FromArgb(248, 224, 242, 254);
        }
        else
        {
            _primaryBrush.Color = lightTheme
                ? Color.FromArgb(218, 5, 150, 105)
                : Color.FromArgb(240, 52, 211, 153);
            _highlightBrush.Color = lightTheme
                ? Color.FromArgb(228, 14, 165, 233)
                : Color.FromArgb(250, 209, 250, 229);
        }
    }

    public void Play(IslandMotionKind kind, int durationMs, bool highContrast)
    {
        Stop();
        if (highContrast || kind == IslandMotionKind.Default || _nodes.Length == 0) return;

        var compositor = _nodes[0].Visual.Compositor;
        using var easeOut = compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 0.86f), new Vector2(0.22f, 1f));
        using var easeIn = compositor.CreateCubicBezierEasingFunction(new Vector2(0.32f, 0.04f), new Vector2(0.24f, 1f));
        using var linear = compositor.CreateLinearEasingFunction();

        var spanW = (float)_width;
        var spanH = (float)_height;
        const float originX = 32f;

        foreach (var (visual, spec) in _nodes)
        {
            var t0 = Math.Clamp(spec.Delay, 0f, 0.85f);
            var tEnd = Math.Clamp(t0 + spec.Span, t0 + 0.12f, 1f);
            var tPeak = t0 + (tEnd - t0) * 0.24f;
            var tMid = t0 + (tEnd - t0) * 0.62f;

            var baseY = Math.Clamp(spanH * spec.SeedYRatio, 14f, spanH - 14f);
            var reachX = originX + (spanW - originX - 18f) * spec.SpreadXRatio;

            Vector3 startPos, midPos, endPos;
            CompositionEasingFunction moveEase;

            if (kind is IslandMotionKind.PowerEngage or IslandMotionKind.PowerPromote)
            {
                // Surge outward from the magnetic battery coupler across the capsule interior
                startPos = new Vector3(originX, baseY, 0f);
                midPos = new Vector3(originX + (reachX - originX) * 0.64f, baseY + spec.DriftY * 0.65f, 0f);
                endPos = new Vector3(reachX, baseY + spec.DriftY, 0f);
                moveEase = easeOut;
            }
            else if (kind == IslandMotionKind.PowerAlert)
            {
                // Urgent warning burst radiating from the battery emblem with crisp upward/outward flare
                var alertReach = originX + (spanW - originX - 24f) * (0.35f + spec.SpreadXRatio * 0.58f);
                startPos = new Vector3(originX - 2f, baseY, 0f);
                midPos = new Vector3(originX + (alertReach - originX) * 0.72f, baseY + spec.DriftY * 0.85f, 0f);
                endPos = new Vector3(alertReach, baseY + spec.DriftY * 1.15f, 0f);
                moveEase = easeOut;
            }
            else
            {
                // Unplug drain: scattered amber embers pulled back left & downward toward the battery
                startPos = new Vector3(reachX, baseY - 4f, 0f);
                midPos = new Vector3(originX + (reachX - originX) * 0.42f, baseY + MathF.Abs(spec.DriftY) * 0.65f + 5f, 0f);
                endPos = new Vector3(originX + 4f, Math.Min(spanH - 10f, baseY + MathF.Abs(spec.DriftY) + 12f), 0f);
                moveEase = easeIn;
            }

            using (var move = compositor.CreateVector3KeyFrameAnimation())
            {
                move.Duration = TimeSpan.FromMilliseconds(durationMs);
                move.InsertKeyFrame(0f, startPos, linear);
                if (t0 > 0f) move.InsertKeyFrame(t0, startPos, linear);
                move.InsertKeyFrame(tMid, midPos, moveEase);
                move.InsertKeyFrame(tEnd, endPos, moveEase);
                if (tEnd < 1f) move.InsertKeyFrame(1f, endPos, linear);
                visual.StartAnimation("Translation", move);
            }

            using (var opacity = compositor.CreateScalarKeyFrameAnimation())
            {
                opacity.Duration = TimeSpan.FromMilliseconds(durationMs);
                opacity.InsertKeyFrame(0f, 0f, linear);
                if (t0 > 0f) opacity.InsertKeyFrame(t0, 0f, linear);
                opacity.InsertKeyFrame(tPeak, spec.PeakOpacity, linear);
                opacity.InsertKeyFrame(tMid, spec.PeakOpacity * 0.72f, linear);
                opacity.InsertKeyFrame(tEnd, 0f, linear);
                if (tEnd < 1f) opacity.InsertKeyFrame(1f, 0f, linear);
                visual.StartAnimation(nameof(Visual.Opacity), opacity);
            }

            using (var scale = compositor.CreateVector3KeyFrameAnimation())
            {
                scale.Duration = TimeSpan.FromMilliseconds(durationMs);
                var s0 = spec.Kind == ParticleShapeKind.IonStreak ? new Vector3(0.35f, 1f, 1f) : new Vector3(0.35f, 0.35f, 1f);
                var sPeak = spec.Kind == ParticleShapeKind.IonStreak ? new Vector3(1.35f, 1f, 1f) : new Vector3(1.22f, 1.22f, 1f);
                var sEnd = spec.Kind == ParticleShapeKind.IonStreak ? new Vector3(0.45f, 0.85f, 1f) : new Vector3(0.42f, 0.42f, 1f);
                scale.InsertKeyFrame(0f, s0, linear);
                if (t0 > 0f) scale.InsertKeyFrame(t0, s0, linear);
                scale.InsertKeyFrame(tPeak, sPeak, easeOut);
                scale.InsertKeyFrame(tEnd, sEnd, linear);
                if (tEnd < 1f) scale.InsertKeyFrame(1f, sEnd, linear);
                visual.StartAnimation(nameof(Visual.Scale), scale);
            }

            if (MathF.Abs(spec.SpinDegrees) > 0.5f)
            {
                using var spin = compositor.CreateScalarKeyFrameAnimation();
                spin.Duration = TimeSpan.FromMilliseconds(durationMs);
                var endAngle = kind == IslandMotionKind.PowerRelease ? -spec.SpinDegrees * 0.75f : spec.SpinDegrees;
                spin.InsertKeyFrame(0f, 0f, linear);
                if (t0 > 0f) spin.InsertKeyFrame(t0, 0f, linear);
                spin.InsertKeyFrame(tEnd, endAngle, moveEase);
                if (tEnd < 1f) spin.InsertKeyFrame(1f, endAngle, linear);
                visual.StartAnimation(nameof(Visual.RotationAngleInDegrees), spin);
            }
        }
    }

    public void Stop()
    {
        foreach (var (visual, _) in _nodes)
        {
            visual.StopAnimation("Translation");
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.StopAnimation(nameof(Visual.Scale));
            visual.StopAnimation(nameof(Visual.RotationAngleInDegrees));
            visual.Opacity = 0f;
            visual.Scale = Vector3.One;
            visual.RotationAngleInDegrees = 0f;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
        }
    }

    private static UIElement CreateShape(ParticleSpec spec, Brush fill)
    {
        if (spec.Kind == ParticleShapeKind.IonStreak)
        {
            return new Rectangle
            {
                Width = spec.SizeX,
                Height = spec.SizeY,
                RadiusX = spec.SizeY * 0.5,
                RadiusY = spec.SizeY * 0.5,
                Fill = fill,
                IsHitTestVisible = false
            };
        }

        if (spec.Kind == ParticleShapeKind.DiamondMote)
        {
            var r = spec.SizeX * 0.5;
            return new XamlPath
            {
                Data = BuildClosedPolygon(new(r, 0), new(spec.SizeX, r), new(r, spec.SizeY), new(0, r)),
                Fill = fill,
                IsHitTestVisible = false
            };
        }

        // Crisp 4-point star glint (sharp points + pinched waist, zero sphere/blur)
        var cx = spec.SizeX * 0.5;
        var cy = spec.SizeY * 0.5;
        var waist = spec.SizeX * 0.16;
        return new XamlPath
        {
            Data = BuildClosedPolygon(
                new(cx, 0),
                new(cx + waist, cy - waist),
                new(spec.SizeX, cy),
                new(cx + waist, cy + waist),
                new(cx, spec.SizeY),
                new(cx - waist, cy + waist),
                new(0, cy),
                new(cx - waist, cy - waist)),
            Fill = fill,
            IsHitTestVisible = false
        };
    }

    private static PathGeometry BuildClosedPolygon(params Point[] points)
    {
        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = points[0], IsClosed = true, IsFilled = true };
        for (var i = 1; i < points.Length; i++)
            figure.Segments.Add(new LineSegment { Point = points[i] });
        geometry.Figures.Add(figure);
        return geometry;
    }
}
