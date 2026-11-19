using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace WindowsIsland.Services;

// 贴合药丸圆角外框的双层霓虹流光环与环绕彗星弧，保持 Trim 始终处于 [0, 1] 区间以避免 DComp 截断。
internal sealed class IslandPowerAura : IDisposable
{
    private const float Pad = 10f, Outset = 1.5f, MaxCorner = 29.5f;
    private readonly UIElement _host;
    private readonly Visual _surface;
    private readonly Compositor _compositor;
    private readonly CompositionPropertySet _shape;
    private readonly ShapeVisual _visual;
    private readonly CompositionRoundedRectangleGeometry _geometry, _cometGeometry;
    private readonly CompositionSpriteShape _sheen, _core, _comet;
    private readonly CompositionLinearGradientBrush _sheenBrush, _coreBrush, _cometBrush;
    private readonly CubicBezierEasingFunction _sweepOut, _drainIn;
    private readonly LinearEasingFunction _linear;
    private bool _disposed;

    public IslandPowerAura(UIElement host, Visual surface, CompositionPropertySet shape, NotificationPosition position)
    {
        _host = host;
        _surface = surface;
        _shape = shape;
        _compositor = surface.Compositor;
        _visual = _compositor.CreateShapeVisual();
        _visual.Opacity = 0;

        _geometry = _compositor.CreateRoundedRectangleGeometry();
        _geometry.Offset = new Vector2(Pad - Outset, Pad - Outset);
        _geometry.TrimOffset = 0f;
        _geometry.TrimStart = 0.5f;
        _geometry.TrimEnd = 0.5f;

        _cometGeometry = _compositor.CreateRoundedRectangleGeometry();
        _cometGeometry.Offset = new Vector2(Pad - Outset, Pad - Outset);
        _cometGeometry.TrimOffset = 0f;
        _cometGeometry.TrimStart = 0.5f;
        _cometGeometry.TrimEnd = 0.5f;

        _sheenBrush = CreateBrush();
        _coreBrush = CreateBrush();
        _cometBrush = CreateBrush();
        _sheen = CreateStroke(_geometry, _sheenBrush, 8.0f);
        _core = CreateStroke(_geometry, _coreBrush, 3.0f);
        _comet = CreateStroke(_cometGeometry, _cometBrush, 4.2f);
        _visual.Shapes.Add(_sheen);
        _visual.Shapes.Add(_core);
        _visual.Shapes.Add(_comet);

        _sweepOut = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 0.88f), new Vector2(0.20f, 1f));
        _drainIn = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.36f, 0.06f), new Vector2(0.22f, 1f));
        _linear = _compositor.CreateLinearEasingFunction();
        ElementCompositionPreview.SetElementChildVisual(_host, _visual);
        BindGeometry();
        SetPosition(position);
    }

    private CompositionLinearGradientBrush CreateBrush()
    {
        var brush = _compositor.CreateLinearGradientBrush();
        brush.MappingMode = CompositionMappingMode.Relative;
        return brush;
    }

    private CompositionSpriteShape CreateStroke(CompositionGeometry geometry, CompositionBrush brush, float thickness)
    {
        var stroke = _compositor.CreateSpriteShape(geometry);
        stroke.StrokeBrush = brush;
        stroke.StrokeThickness = thickness;
        stroke.StrokeStartCap = CompositionStrokeCap.Round;
        stroke.StrokeEndCap = CompositionStrokeCap.Round;
        return stroke;
    }

    private void BindGeometry()
    {
        using var size = _compositor.CreateExpressionAnimation(
            $"Vector2(Max(1f, shape.Size.X + {Pad * 2:0.0}f), Max(1f, shape.Size.Y + {Pad * 2:0.0}f))");
        size.SetReferenceParameter("shape", _shape);
        _visual.StartAnimation(nameof(Visual.Size), size);

        using var rectSize = _compositor.CreateExpressionAnimation(
            $"Vector2(Max(1f, shape.Size.X + {Outset * 2:0.0}f), Max(1f, shape.Size.Y + {Outset * 2:0.0}f))");
        rectSize.SetReferenceParameter("shape", _shape);
        _geometry.StartAnimation(nameof(CompositionRoundedRectangleGeometry.Size), rectSize);
        _cometGeometry.StartAnimation(nameof(CompositionRoundedRectangleGeometry.Size), rectSize);

        using var radius = _compositor.CreateExpressionAnimation(
            $"Vector2(Min({MaxCorner:0.0}f, Min(Max(1f, shape.Size.X + {Outset * 2:0.0}f), Max(1f, shape.Size.Y + {Outset * 2:0.0}f)) * 0.5f), " +
            $"Min({MaxCorner:0.0}f, Min(Max(1f, shape.Size.X + {Outset * 2:0.0}f), Max(1f, shape.Size.Y + {Outset * 2:0.0}f)) * 0.5f))");
        radius.SetReferenceParameter("shape", _shape);
        _geometry.StartAnimation(nameof(CompositionRoundedRectangleGeometry.CornerRadius), radius);
        _cometGeometry.StartAnimation(nameof(CompositionRoundedRectangleGeometry.CornerRadius), radius);
    }

    public void SetPosition(NotificationPosition position)
    {
        if (_disposed) return;
        var anchor = NotificationPlacement.Anchor(position);
        var opposite = Vector2.One - anchor;
        _sheenBrush.StartPoint = _coreBrush.StartPoint = _cometBrush.StartPoint = anchor;
        _sheenBrush.EndPoint = _coreBrush.EndPoint = _cometBrush.EndPoint = opposite;

        using var offset = _compositor.CreateExpressionAnimation(
            $"Vector3(surface.Offset.X + (surface.Size.X - shape.Size.X) * anchor.X + surfaceProps.Translation.X - {Pad:0.0}f, " +
            $"surface.Offset.Y + (surface.Size.Y - shape.Size.Y) * anchor.Y + surfaceProps.Translation.Y - {Pad:0.0}f, 0f)");
        offset.SetReferenceParameter("surface", _surface);
        offset.SetReferenceParameter("surfaceProps", _surface.Properties);
        offset.SetReferenceParameter("shape", _shape);
        offset.SetVector2Parameter("anchor", anchor);
        _visual.StartAnimation(nameof(Visual.Offset), offset);
    }

    public void Play(IslandMotionKind kind, int durationMs, bool lightTheme, bool highContrast, bool opacityTransitions)
    {
        if (_disposed) return;
        Stop();
        if (highContrast || kind == IslandMotionKind.Default) return;
        ConfigurePalette(kind, lightTheme);
        if (kind == IslandMotionKind.PowerEngage)
        {
            // 双侧展开包覆全胶囊并持续辉光，末端双端对称收拢消散（即使关闭透明度过渡也会自然归零）。
            AnimateScalar(_geometry, nameof(CompositionGeometry.TrimStart), durationMs, _sweepOut,
                (0f, 0.50f), (0.22f, 0.00f), (0.82f, 0.00f), (1f, 0.50f));
            AnimateScalar(_geometry, nameof(CompositionGeometry.TrimEnd), durationMs, _sweepOut,
                (0f, 0.50f), (0.22f, 1.00f), (0.82f, 1.00f), (1f, 0.50f));

            // 高亮能量彗星环绕胶囊飞掠两段流光轨迹（全程严格位于 [0, 1]）。
            AnimateScalar(_cometGeometry, nameof(CompositionGeometry.TrimStart), durationMs, _linear,
                (0f, 0.00f), (0.10f, 0.00f), (0.50f, 0.76f), (0.52f, 0.00f), (0.92f, 0.78f), (1f, 1.00f));
            AnimateScalar(_cometGeometry, nameof(CompositionGeometry.TrimEnd), durationMs, _linear,
                (0f, 0.14f), (0.10f, 0.24f), (0.50f, 1.00f), (0.52f, 0.22f), (0.92f, 1.00f), (1f, 1.00f));

            AnimateScalar(_core, nameof(CompositionSpriteShape.StrokeThickness), durationMs, _sweepOut,
                (0f, 3.6f), (0.35f, 3.0f), (0.78f, 2.4f), (1f, 0f));
            AnimateScalar(_sheen, nameof(CompositionSpriteShape.StrokeThickness), durationMs, _sweepOut,
                (0f, 9.2f), (0.35f, 7.6f), (0.78f, 5.8f), (1f, 0f));
            AnimateScalar(_comet, nameof(CompositionSpriteShape.StrokeThickness), durationMs, _sweepOut,
                (0f, 4.8f), (0.50f, 4.2f), (0.86f, 2.4f), (1f, 0f));

            if (opacityTransitions)
                AnimateScalar(_visual, nameof(Visual.Opacity), durationMs, _linear,
                    (0f, 0.96f), (0.30f, 1.00f), (0.76f, 0.90f), (1f, 0f));
            else _visual.Opacity = 1f;
        }
        else if (kind == IslandMotionKind.PowerPromote)
        {
            // 原位接力升级：保持胶囊完整翡翠绿流光不闪烁中断，单圈高亮彗星平顺掠过并收拢。
            AnimateScalar(_geometry, nameof(CompositionGeometry.TrimStart), durationMs, _sweepOut,
                (0f, 0.00f), (0.80f, 0.00f), (1f, 0.50f));
            AnimateScalar(_geometry, nameof(CompositionGeometry.TrimEnd), durationMs, _sweepOut,
                (0f, 1.00f), (0.80f, 1.00f), (1f, 0.50f));

            AnimateScalar(_cometGeometry, nameof(CompositionGeometry.TrimStart), durationMs, _linear,
                (0f, 0.00f), (0.78f, 0.78f), (1f, 1.00f));
            AnimateScalar(_cometGeometry, nameof(CompositionGeometry.TrimEnd), durationMs, _linear,
                (0f, 0.22f), (0.78f, 1.00f), (1f, 1.00f));

            AnimateScalar(_core, nameof(CompositionSpriteShape.StrokeThickness), durationMs, _sweepOut,
                (0f, 3.0f), (0.45f, 2.8f), (0.82f, 2.2f), (1f, 0f));
            AnimateScalar(_sheen, nameof(CompositionSpriteShape.StrokeThickness), durationMs, _sweepOut,
                (0f, 7.8f), (0.45f, 7.0f), (0.82f, 5.2f), (1f, 0f));
            AnimateScalar(_comet, nameof(CompositionSpriteShape.StrokeThickness), durationMs, _sweepOut,
                (0f, 4.4f), (0.60f, 3.8f), (1f, 0f));

            if (opacityTransitions)
                AnimateScalar(_visual, nameof(Visual.Opacity), durationMs, _linear,
                    (0f, 0.92f), (0.28f, 1.00f), (0.78f, 0.85f), (1f, 0f));
            else _visual.Opacity = 1f;
        }
        else if (kind == IslandMotionKind.PowerRelease)
        {
            AnimateScalar(_geometry, nameof(CompositionGeometry.TrimStart), durationMs, _drainIn,
                (0f, 0.00f), (0.20f, 0.00f), (0.94f, 0.50f), (1f, 0.50f));
            AnimateScalar(_geometry, nameof(CompositionGeometry.TrimEnd), durationMs, _drainIn,
                (0f, 1.00f), (0.20f, 1.00f), (0.94f, 0.50f), (1f, 0.50f));

            AnimateScalar(_cometGeometry, nameof(CompositionGeometry.TrimStart), durationMs, _drainIn,
                (0f, 0.74f), (0.88f, 0.48f), (1f, 0.50f));
            AnimateScalar(_cometGeometry, nameof(CompositionGeometry.TrimEnd), durationMs, _drainIn,
                (0f, 1.00f), (0.88f, 0.52f), (1f, 0.50f));

            AnimateScalar(_core, nameof(CompositionSpriteShape.StrokeThickness), durationMs, _drainIn,
                (0f, 3.4f), (0.35f, 2.8f), (0.82f, 1.8f), (1f, 0f));
            AnimateScalar(_sheen, nameof(CompositionSpriteShape.StrokeThickness), durationMs, _drainIn,
                (0f, 8.6f), (0.35f, 6.8f), (0.82f, 4.2f), (1f, 0f));
            AnimateScalar(_comet, nameof(CompositionSpriteShape.StrokeThickness), durationMs, _drainIn,
                (0f, 4.4f), (0.55f, 3.2f), (1f, 0f));

            if (opacityTransitions)
                AnimateScalar(_visual, nameof(Visual.Opacity), durationMs, _linear,
                    (0f, 0.96f), (0.30f, 0.94f), (0.80f, 0.52f), (1f, 0f));
            else _visual.Opacity = 1f;
        }
        else if (kind == IslandMotionKind.PowerAlert)
        {
            // 双脉冲珊瑚红低电警示光环，快速包覆胶囊并两次律动提醒。
            AnimateScalar(_geometry, nameof(CompositionGeometry.TrimStart), durationMs, _sweepOut,
                (0f, 0.50f), (0.16f, 0.00f), (0.80f, 0.00f), (1f, 0.50f));
            AnimateScalar(_geometry, nameof(CompositionGeometry.TrimEnd), durationMs, _sweepOut,
                (0f, 0.50f), (0.16f, 1.00f), (0.80f, 1.00f), (1f, 0.50f));

            AnimateScalar(_cometGeometry, nameof(CompositionGeometry.TrimStart), durationMs, _linear,
                (0f, 0.00f), (0.44f, 0.78f), (0.48f, 0.00f), (0.90f, 0.78f), (1f, 1.00f));
            AnimateScalar(_cometGeometry, nameof(CompositionGeometry.TrimEnd), durationMs, _linear,
                (0f, 0.22f), (0.44f, 1.00f), (0.48f, 0.22f), (0.90f, 1.00f), (1f, 1.00f));

            AnimateScalar(_core, nameof(CompositionSpriteShape.StrokeThickness), durationMs, _sweepOut,
                (0f, 3.8f), (0.28f, 2.4f), (0.52f, 3.6f), (0.82f, 2.0f), (1f, 0f));
            AnimateScalar(_sheen, nameof(CompositionSpriteShape.StrokeThickness), durationMs, _sweepOut,
                (0f, 9.6f), (0.28f, 6.2f), (0.52f, 9.0f), (0.82f, 5.0f), (1f, 0f));
            AnimateScalar(_comet, nameof(CompositionSpriteShape.StrokeThickness), durationMs, _sweepOut,
                (0f, 4.8f), (0.52f, 4.2f), (0.86f, 2.2f), (1f, 0f));

            if (opacityTransitions)
                AnimateScalar(_visual, nameof(Visual.Opacity), durationMs, _linear,
                    (0f, 0.98f), (0.26f, 0.72f), (0.50f, 1.00f), (0.80f, 0.65f), (1f, 0f));
            else _visual.Opacity = 1f;
        }
    }

    private void ConfigurePalette(IslandMotionKind kind, bool lightTheme)
    {
        _coreBrush.ColorStops.Clear();
        _sheenBrush.ColorStops.Clear();
        _cometBrush.ColorStops.Clear();
        if (kind is IslandMotionKind.PowerEngage or IslandMotionKind.PowerPromote)
        {
            var start = lightTheme ? Color.FromArgb(255, 5, 150, 105) : Color.FromArgb(255, 16, 235, 160);
            var mid = lightTheme ? Color.FromArgb(255, 14, 165, 233) : Color.FromArgb(255, 34, 211, 238);
            var end = lightTheme ? Color.FromArgb(255, 16, 185, 129) : Color.FromArgb(255, 167, 243, 208);
            var flash = lightTheme ? Color.FromArgb(255, 110, 231, 183) : Color.FromArgb(255, 236, 253, 245);
            _coreBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0f, start));
            _coreBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.52f, mid));
            _coreBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1f, end));
            _sheenBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0f, WithAlpha(start, 155)));
            _sheenBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.5f, WithAlpha(mid, 135)));
            _sheenBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1f, WithAlpha(end, 120)));
            _cometBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0f, WithAlpha(mid, 60)));
            _cometBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.75f, flash));
            _cometBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1f, flash));
        }
        else if (kind == IslandMotionKind.PowerAlert)
        {
            var start = lightTheme ? Color.FromArgb(255, 220, 38, 38) : Color.FromArgb(255, 248, 113, 113);
            var mid = lightTheme ? Color.FromArgb(255, 225, 29, 72) : Color.FromArgb(255, 251, 113, 133);
            var end = lightTheme ? Color.FromArgb(245, 185, 28, 28) : Color.FromArgb(250, 254, 202, 202);
            var flash = lightTheme ? Color.FromArgb(255, 254, 202, 202) : Color.FromArgb(255, 255, 241, 242);
            _coreBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0f, start));
            _coreBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.52f, mid));
            _coreBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1f, end));
            _sheenBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0f, WithAlpha(start, 160)));
            _sheenBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.5f, WithAlpha(mid, 140)));
            _sheenBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1f, WithAlpha(end, 115)));
            _cometBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0f, WithAlpha(mid, 65)));
            _cometBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.75f, flash));
            _cometBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1f, flash));
        }
        else
        {
            var start = lightTheme ? Color.FromArgb(255, 217, 119, 6) : Color.FromArgb(255, 251, 146, 60);
            var mid = lightTheme ? Color.FromArgb(255, 234, 88, 12) : Color.FromArgb(255, 251, 191, 36);
            var end = lightTheme ? Color.FromArgb(240, 180, 83, 9) : Color.FromArgb(245, 254, 240, 138);
            var flash = lightTheme ? Color.FromArgb(255, 253, 186, 116) : Color.FromArgb(255, 254, 249, 195);
            _coreBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0f, start));
            _coreBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.55f, mid));
            _coreBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1f, end));
            _sheenBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0f, WithAlpha(start, 150)));
            _sheenBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.5f, WithAlpha(mid, 130)));
            _sheenBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1f, WithAlpha(end, 105)));
            _cometBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0f, flash));
            _cometBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1f, WithAlpha(mid, 60)));
        }
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private void AnimateScalar(CompositionObject target, string property, int milliseconds, CompositionEasingFunction easing,
        params (float At, float Value)[] frames)
    {
        using var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        animation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        foreach (var (at, value) in frames) animation.InsertKeyFrame(at, value, easing);
        target.StartAnimation(property, animation);
    }

    public void Stop()
    {
        if (_disposed) return;
        _geometry.StopAnimation(nameof(CompositionGeometry.TrimStart));
        _geometry.StopAnimation(nameof(CompositionGeometry.TrimEnd));
        _cometGeometry.StopAnimation(nameof(CompositionGeometry.TrimStart));
        _cometGeometry.StopAnimation(nameof(CompositionGeometry.TrimEnd));
        _core.StopAnimation(nameof(CompositionSpriteShape.StrokeThickness));
        _sheen.StopAnimation(nameof(CompositionSpriteShape.StrokeThickness));
        _comet.StopAnimation(nameof(CompositionSpriteShape.StrokeThickness));
        _visual.StopAnimation(nameof(Visual.Opacity));
        _visual.Opacity = 0;
        _geometry.TrimStart = 0.5f;
        _geometry.TrimEnd = 0.5f;
        _cometGeometry.TrimStart = 0.5f;
        _cometGeometry.TrimEnd = 0.5f;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        _visual.StopAnimation(nameof(Visual.Size));
        _visual.StopAnimation(nameof(Visual.Offset));
        _geometry.StopAnimation(nameof(CompositionRoundedRectangleGeometry.Size));
        _geometry.StopAnimation(nameof(CompositionRoundedRectangleGeometry.CornerRadius));
        _cometGeometry.StopAnimation(nameof(CompositionRoundedRectangleGeometry.Size));
        _cometGeometry.StopAnimation(nameof(CompositionRoundedRectangleGeometry.CornerRadius));
        ElementCompositionPreview.SetElementChildVisual(_host, null);
        _comet.Dispose();
        _sheen.Dispose();
        _core.Dispose();
        _cometBrush.Dispose();
        _sheenBrush.Dispose();
        _coreBrush.Dispose();
        _cometGeometry.Dispose();
        _geometry.Dispose();
        _visual.Dispose();
        _sweepOut.Dispose();
        _drainIn.Dispose();
        _linear.Dispose();
    }
}
