using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using WindowsIsland.Components;

namespace WindowsIsland.Services;

internal sealed class IslandMotion : IDisposable
{
    private enum Phase { Hidden, Opening, Visible, Closing }
    private const int OpenDuration = 480, CloseDuration = 460, UpdateDuration = 260;
    private readonly Visual _surface, _content;
    private readonly Visual? _badge;
    private readonly MessageAvatar? _avatar;
    private readonly NotificationPanel? _panel;
    private readonly IslandPowerAura? _aura;
    private readonly Compositor _compositor;
    private readonly CompositionPropertySet _shape;
    private readonly CubicBezierEasingFunction _expand, _contract;
    private readonly CompositionEasingFunction _opacity;
    private readonly Func<bool> _animationsEnabled, _opacityTransitionsEnabled;
    private readonly Func<(bool LightTheme, bool HighContrast)>? _appearance;
    private readonly float _inset;
    private NotificationPosition _position;
    private Vector2 LineSize => NotificationPlacement.LineSize(_position);
    private Vector3 LinePosition => new(NotificationPlacement.EdgeTranslation(_position, _inset), 0);
    private CompositionScopedBatch? _batch;
    private Vector2 _target;
    private Phase _phase;
    private int _generation;
    private bool _disposed;
    public bool IsRunning { get; private set; }
    public event Action? Settled;

    public IslandMotion(FrameworkElement surface, FrameworkElement content, double inset,
        Func<bool> animationsEnabled, Func<bool> opacityTransitionsEnabled,
        NotificationPosition position = NotificationPosition.TopCenter,
        UIElement? auraHost = null, FrameworkElement? badge = null,
        Func<(bool LightTheme, bool HighContrast)>? appearance = null)
    {
        _surface = ElementCompositionPreview.GetElementVisual(surface);
        _content = ElementCompositionPreview.GetElementVisual(content);
        ElementCompositionPreview.SetIsTranslationEnabled(surface, true);
        ElementCompositionPreview.SetIsTranslationEnabled(content, true);
        _surface.Properties.InsertVector3("Translation", Vector3.Zero);
        _content.Properties.InsertVector3("Translation", Vector3.Zero);
        if (badge is not null)
        {
            _avatar = badge as MessageAvatar;
            _panel = badge.Parent as NotificationPanel;
            _badge = ElementCompositionPreview.GetElementVisual(badge);
            ElementCompositionPreview.SetIsTranslationEnabled(badge, true);
            _badge.Properties.InsertVector3("Translation", Vector3.Zero);
            _badge.CenterPoint = new Vector3(
                (float)(badge.Width > 0 ? badge.Width * 0.5 : 20),
                (float)(badge.Height > 0 ? badge.Height * 0.5 : 20), 0);
        }
        _compositor = _surface.Compositor;
        _animationsEnabled = animationsEnabled;
        _opacityTransitionsEnabled = opacityTransitionsEnabled;
        _appearance = appearance;
        _inset = (float)inset;
        _shape = _compositor.CreatePropertySet();
        _shape.InsertVector2("Size", Vector2.One);
        _expand = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 0.8f), new Vector2(0.18f, 1));
        _contract = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0), new Vector2(0.2f, 1));
        _opacity = _compositor.CreateLinearEasingFunction();
        if (auraHost is not null) _aura = new IslandPowerAura(auraHost, _surface, _shape, position);
        SetPosition(position);
        Reset();
    }

    public void SetPosition(NotificationPosition position)
    {
        _position = NotificationPlacement.Normalize(position);
        using var alignment = _compositor.CreateExpressionAnimation("Vector3(container.Size.X * anchor.X, container.Size.Y * anchor.Y, 0)");
        alignment.SetReferenceParameter("container", _surface);
        alignment.SetVector2Parameter("anchor", NotificationPlacement.Anchor(_position));
        _surface.StartAnimation(nameof(Visual.CenterPoint), alignment);
        _aura?.SetPosition(_position);
    }

    public void PrimeColdOpen(Size target, IslandMotionKind kind)
    {
        if (_disposed) return;
        CancelCompletion();
        StopAnimations(keepPowerEffects: false);
        _target = new Vector2((float)target.Width, (float)target.Height);
        var seed = kind == IslandMotionKind.PowerEngage
            ? PowerMotionProfile.IgnitionLine(_position, _target) : LineSize;
        _shape.InsertVector2("Size", seed);
        _surface.Opacity = _opacityTransitionsEnabled() ? 0 : 1;
        _content.Opacity = 1;
        _surface.Properties.InsertVector3("Translation", LinePosition);
        var entryOffset = kind == IslandMotionKind.PowerEngage ? -14f : kind == IslandMotionKind.PowerRelease ? -6f : -6f;
        _content.Properties.InsertVector3("Translation", new Vector3(NotificationPlacement.Outward(_position) * entryOffset, 0));
        ScaleSurface();
        _phase = Phase.Hidden;
    }

    public void Show(Size previous, Size target, bool visible, bool newMessage, bool animate = true,
        IslandMotionKind kind = IslandMotionKind.Default)
    {
        if (_disposed) return;
        var opening = !visible || _phase == Phase.Hidden;
        var settled = _phase == Phase.Visible;
        CancelCompletion();
        _target = new Vector2((float)target.Width, (float)target.Height);
        if (opening)
        {
            StopAnimations(keepPowerEffects: false);
            var seed = kind == IslandMotionKind.PowerEngage
                ? PowerMotionProfile.IgnitionLine(_position, _target) : LineSize;
            _shape.InsertVector2("Size", seed);
            _surface.Opacity = _opacityTransitionsEnabled() ? 0 : 1;
            _content.Opacity = 1;
            _surface.Properties.InsertVector3("Translation", LinePosition);
            var entryOffset = kind == IslandMotionKind.PowerEngage ? -14f : kind == IslandMotionKind.PowerRelease ? -6f : -6f;
            _content.Properties.InsertVector3("Translation", new Vector3(NotificationPlacement.Outward(_position) * entryOffset, 0));
        }
        else if (settled) _shape.InsertVector2("Size", new Vector2((float)previous.Width, (float)previous.Height));
        if (!animate || !_animationsEnabled()) { SetVisible(keepPowerEffects: false); return; }
        _aura?.Stop();
        if (kind == IslandMotionKind.PowerPromote && !opening)
        {
            if (_panel is not null) _panel.StopPowerMotion();
            else _avatar?.StopPowerMotion();
        }
        else ResetBadge(keepPowerEffects: false);
        ScaleSurface();
        _phase = Phase.Opening;
        Begin(() =>
        {
            if (kind == IslandMotionKind.PowerEngage) AnimatePowerEngage(opening, settled);
            else if (kind == IslandMotionKind.PowerPromote) AnimatePowerPromote(opening, settled);
            else if (kind is IslandMotionKind.PowerRelease or IslandMotionKind.PowerAlert) AnimatePowerRelease(opening, settled);
            else AnimateDefaultShow(opening, newMessage, settled);
        }, () => SetVisible(keepPowerEffects: kind != IslandMotionKind.Default));
        if (kind is IslandMotionKind.PowerEngage or IslandMotionKind.PowerPromote or IslandMotionKind.PowerRelease or IslandMotionKind.PowerAlert)
        {
            var effectDuration = kind is IslandMotionKind.PowerEngage or IslandMotionKind.PowerPromote
                ? PowerMotionProfile.AuraEngageDuration : PowerMotionProfile.AuraReleaseDuration;
            var (light, highContrast) = _appearance?.Invoke() ?? (false, false);
            var opacityAllowed = _opacityTransitionsEnabled();
            if (_panel is not null) _panel.PlayPowerMotion(kind, effectDuration, light, highContrast, opacityAllowed);
            else _avatar?.PlayPowerMotion(kind, effectDuration);
            _aura?.Play(kind, effectDuration, light, highContrast, opacityAllowed);
        }
    }

    private void AnimateDefaultShow(bool opening, bool newMessage, bool settled)
    {
        if (opening)
        {
            AnimateShape(OpenDuration, _expand, 0, (0.14f, LineSize), (0.26f, Shoulder()), (1, _target));
            AnimateOpacity(_surface, OpenDuration, (1, 1));
            Animate(_surface, "Translation", OpenDuration, _expand, (0.14f, LinePosition), (1, Vector3.Zero));
            Animate(_content, "Translation", Vector3.Zero, OpenDuration, _expand);
        }
        else
        {
            // StartingValue also covers an arrival during the previous opening or closing motion.
            AnimateShape(UpdateDuration, _expand, 0, (1, _target));
            AnimateOpacity(_surface, UpdateDuration, (1, 1));
            Animate(_surface, "Translation", Vector3.Zero, UpdateDuration, _expand);
            if (newMessage && settled)
                AnimateOpacity(_content, UpdateDuration, (0.16f, 0.45f), (1, 1));
            else AnimateOpacity(_content, UpdateDuration, (1, 1));
            Animate(_content, "Translation", Vector3.Zero, UpdateDuration, _expand);
        }
    }

    private void AnimatePowerEngage(bool opening, bool settled)
    {
        var duration = opening ? PowerMotionProfile.EngageOpenDuration : PowerMotionProfile.EngageUpdateDuration;
        var inward = new Vector3(PowerMotionProfile.Inward(_position), 0);
        var surge = PowerMotionProfile.Deform(_position, _target, 1.15f, 1.12f);
        var recoil = PowerMotionProfile.Deform(_position, _target, 0.952f, 0.958f);
        var cushion = PowerMotionProfile.Deform(_position, _target, 1.018f, 1.014f);
        if (opening)
        {
            var ignition = PowerMotionProfile.IgnitionLine(_position, _target);
            AnimateShape(duration, _expand, 0.06f,
                (0.14f, ignition), (0.40f, surge), (0.64f, recoil), (0.84f, cushion), (1f, _target));
            AnimateOpacity(_surface, duration, (0.06f, 0.92f), (0.28f, 1f), (1f, 1f));
            Animate(_surface, "Translation", duration, _expand,
                (0.06f, LinePosition * 0.82f), (0.40f, inward * 18.5f),
                (0.64f, inward * -4.2f), (0.84f, inward * 1.6f), (1f, Vector3.Zero));
            Animate(_content, "Translation", duration, _expand,
                (0.40f, inward * 4.5f), (0.66f, inward * -1.2f), (1f, Vector3.Zero));
        }
        else
        {
            var dock = PowerMotionProfile.Deform(_position, _target, 1.05f, 0.90f);
            AnimateShape(duration, _expand, 0,
                (0.14f, dock), (0.42f, surge), (0.68f, recoil), (0.86f, cushion), (1f, _target));
            AnimateOpacity(_surface, duration, (1f, 1f));
            Animate(_surface, "Translation", duration, _expand,
                (0.14f, inward * -5.5f), (0.42f, inward * 16.5f),
                (0.68f, inward * -3.8f), (0.86f, inward * 1.4f), (1f, Vector3.Zero));
            if (settled) AnimateOpacity(_content, duration, (0.14f, 0.55f), (1f, 1f));
            else AnimateOpacity(_content, duration, (1f, 1f));
            Animate(_content, "Translation", duration, _expand,
                (0.14f, inward * -2.5f), (0.42f, inward * 4.0f), (1f, Vector3.Zero));
        }
        if (_badge is not null)
        {
            _badge.Scale = new Vector3(0.66f, 0.66f, 1f);
            _badge.RotationAngleInDegrees = -8.0f;
            Animate(_badge, nameof(Visual.Scale), duration, _expand,
                (0.38f, new Vector3(1.22f, 1.22f, 1f)),
                (0.64f, new Vector3(0.92f, 0.92f, 1f)),
                (0.84f, new Vector3(1.04f, 1.04f, 1f)),
                (1f, Vector3.One));
            Animate(_badge, nameof(Visual.RotationAngleInDegrees), duration, _expand,
                (0.38f, 4.2f), (0.68f, -1.6f), (1f, 0f));
            Animate(_badge, "Translation", duration, _expand,
                (0.12f, inward * -5.0f), (0.40f, inward * 4.2f), (1f, Vector3.Zero));
        }
    }

    private void AnimatePowerPromote(bool opening, bool settled)
    {
        if (opening)
        {
            AnimatePowerEngage(opening: true, settled);
            return;
        }

        // 「已连接电源 → 开始充电 / 已达充电上限」原位接力：不产生第二次窗体弹跳，仅平滑贴合新宽度并轻柔呼吸。
        const int duration = 420;
        if (settled)
        {
            var whisper = PowerMotionProfile.Deform(_position, _target, 1.022f, 1.010f);
            AnimateShape(duration, _expand, 0, (0.44f, whisper), (1f, _target));
        }
        else
        {
            AnimateShape(duration, _expand, 0, (1f, _target));
        }
        AnimateOpacity(_surface, duration, (1f, 1f));
        Animate(_surface, "Translation", Vector3.Zero, duration, _expand);
        AnimateOpacity(_content, duration, (1f, 1f));
        Animate(_content, "Translation", Vector3.Zero, duration, _expand);
        if (_badge is not null)
        {
            Animate(_badge, nameof(Visual.Scale), duration, _expand,
                (0.42f, new Vector3(1.06f, 1.06f, 1f)), (1f, Vector3.One));
            Animate(_badge, nameof(Visual.RotationAngleInDegrees), duration, _expand, (1f, 0f));
            Animate(_badge, "Translation", Vector3.Zero, duration, _expand);
        }
    }

    private void AnimatePowerRelease(bool opening, bool settled)
    {
        var duration = opening ? PowerMotionProfile.ReleaseOpenDuration : PowerMotionProfile.ReleaseUpdateDuration;
        var inward = new Vector3(PowerMotionProfile.Inward(_position), 0);
        var lateral = new Vector3(PowerMotionProfile.Lateral(_position), 0);
        var tug = PowerMotionProfile.Deform(_position, _target, 0.865f, 1.135f);
        var snapSquash = PowerMotionProfile.Deform(_position, _target, 1.08f, 0.915f);
        var rebound = PowerMotionProfile.Deform(_position, _target, 0.975f, 1.02f);
        if (opening)
        {
            AnimateShape(duration, _expand, 0.06f,
                (0.12f, LineSize), (0.28f, tug), (0.50f, snapSquash), (0.72f, rebound), (1f, _target));
            AnimateOpacity(_surface, duration, (0.06f, 0.90f), (0.26f, 1f), (1f, 1f));
            Animate(_surface, "Translation", duration, _expand,
                (0.10f, LinePosition * 0.78f),
                (0.26f, inward * 2.5f - lateral * 3.0f),
                (0.48f, inward * 20.0f + lateral * 7.2f),
                (0.66f, inward * -3.8f - lateral * 5.0f),
                (0.82f, inward * 1.6f + lateral * 1.8f),
                (1f, Vector3.Zero));
            Animate(_content, "Translation", duration, _expand,
                (0.48f, inward * 5.0f + lateral * 2.5f),
                (0.70f, inward * -1.4f - lateral * 1.4f),
                (1f, Vector3.Zero));
        }
        else
        {
            AnimateShape(duration, _expand, 0,
                (0.20f, tug), (0.44f, snapSquash), (0.68f, rebound), (1f, _target));
            AnimateOpacity(_surface, duration, (1f, 1f));
            Animate(_surface, "Translation", duration, _expand,
                (0.18f, inward * -6.0f - lateral * 3.2f),
                (0.42f, inward * 18.5f + lateral * 7.5f),
                (0.62f, inward * -3.6f - lateral * 5.0f),
                (0.80f, inward * 1.5f + lateral * 1.6f),
                (1f, Vector3.Zero));
            if (settled) AnimateOpacity(_content, duration, (0.14f, 0.58f), (1f, 1f));
            else AnimateOpacity(_content, duration, (1f, 1f));
            Animate(_content, "Translation", duration, _expand,
                (0.42f, inward * 4.5f + lateral * 2.4f),
                (0.66f, inward * -1.2f - lateral * 1.2f),
                (1f, Vector3.Zero));
        }
        if (_badge is not null)
        {
            _badge.Scale = new Vector3(1.12f, 1.12f, 1f);
            _badge.RotationAngleInDegrees = 0f;
            Animate(_badge, nameof(Visual.Scale), duration, _contract,
                (0.24f, new Vector3(0.84f, 0.84f, 1f)),
                (0.48f, new Vector3(1.10f, 1.10f, 1f)),
                (0.74f, new Vector3(0.97f, 0.97f, 1f)),
                (1f, Vector3.One));
            Animate(_badge, nameof(Visual.RotationAngleInDegrees), duration, _contract,
                (0.24f, 10.5f), (0.50f, -6.8f), (0.76f, 2.4f), (1f, 0f));
            Animate(_badge, "Translation", duration, _contract,
                (0.22f, inward * -3.5f), (0.46f, inward * 5.5f), (0.72f, inward * -1.2f), (1f, Vector3.Zero));
        }
    }

    public void Hide(Action completed, bool animate = true)
    {
        if (_disposed) return;
        var settled = _phase == Phase.Visible;
        CancelCompletion();
        _aura?.Stop();
        ResetBadge(keepPowerEffects: false);
        if (!animate || !_animationsEnabled()) { Reset(); completed(); return; }
        _content.StopAnimation(nameof(Visual.Opacity));
        _content.Opacity = 1;
        if (settled) _shape.InsertVector2("Size", _target);
        ScaleSurface();
        _phase = Phase.Closing;
        Begin(() =>
        {
            Animate(_content, "Translation", new Vector3(NotificationPlacement.Outward(_position) * 4, 0), 70, _contract);
            AnimateShape(CloseDuration, _contract, 0.16f, (0.60f, Shoulder()), (0.74f, LineSize), (1, LineSize));
            Animate(_surface, "Translation", CloseDuration, _contract, (0.74f, LinePosition), (1, LinePosition));
            AnimateOpacity(_surface, CloseDuration, (1, 0));
        }, completed);
    }

    public void RefreshOpacityTransitions()
    {
        if (_disposed) return;
        _surface.StopAnimation(nameof(Visual.Opacity));
        _content.StopAnimation(nameof(Visual.Opacity));
        _surface.Opacity = _phase == Phase.Hidden ? 0 : 1;
        _content.Opacity = 1;
    }

    private void AnimateOpacity(Visual visual, int milliseconds, params (float At, float Value)[] frames)
    {
        if (_opacityTransitionsEnabled()) Animate(visual, nameof(Visual.Opacity), milliseconds, _opacity, frames);
        else
        {
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.Opacity = 1;
        }
    }

    private Vector2 Shoulder() => NotificationPlacement.Shoulder(_position, _target);

    private void ScaleSurface()
    {
        using var scale = _compositor.CreateExpressionAnimation(
            "Vector3(shape.Size.X / Max(container.Size.X, 1), shape.Size.Y / Max(container.Size.Y, 1), 1)");
        scale.SetReferenceParameter("shape", _shape);
        scale.SetReferenceParameter("container", _surface);
        _surface.StartAnimation(nameof(Visual.Scale), scale);
    }

    private void Begin(Action animations, Action completed)
    {
        var generation = _generation;
        var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _batch = batch;
        IsRunning = true;
        batch.Completed += (_, _) =>
        {
            if (_disposed || generation != _generation) return;
            IsRunning = false;
            _batch = null;
            completed();
            batch.Dispose();
        };
        animations();
        batch.End();
    }

    private void AnimateShape(int milliseconds, CompositionEasingFunction easing, float hold, params (float At, Vector2 Value)[] frames)
    {
        using var animation = _compositor.CreateVector2KeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        animation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        if (hold > 0) animation.InsertExpressionKeyFrame(hold, "this.StartingValue");
        foreach (var frame in frames) animation.InsertKeyFrame(frame.At, frame.Value, easing);
        _shape.StartAnimation("Size", animation);
    }

    private void Animate(Visual visual, string property, int milliseconds, CompositionEasingFunction easing, params (float At, float Value)[] frames)
    {
        using var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        animation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        foreach (var frame in frames) animation.InsertKeyFrame(frame.At, frame.Value, easing);
        visual.StartAnimation(property, animation);
    }

    private void Animate(Visual visual, string property, Vector3 target, int milliseconds, CompositionEasingFunction easing)
        => Animate(visual, property, milliseconds, easing, (1, target));

    private void Animate(Visual visual, string property, int milliseconds, CompositionEasingFunction easing, params (float At, Vector3 Value)[] frames)
    {
        using var animation = _compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        animation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        foreach (var frame in frames) animation.InsertKeyFrame(frame.At, frame.Value, easing);
        visual.StartAnimation(property, animation);
    }

    private void CancelCompletion()
    {
        _generation++;
        _batch?.Dispose();
        _batch = null;
        IsRunning = false;
    }

    private void ResetBadge(bool keepPowerEffects)
    {
        if (!keepPowerEffects)
        {
            if (_panel is not null) _panel.StopPowerMotion();
            else _avatar?.StopPowerMotion();
        }
        if (_badge is null) return;
        _badge.StopAnimation(nameof(Visual.Scale));
        _badge.StopAnimation(nameof(Visual.RotationAngleInDegrees));
        _badge.StopAnimation("Translation");
        _badge.Scale = Vector3.One;
        _badge.RotationAngleInDegrees = 0f;
        _badge.Properties.InsertVector3("Translation", Vector3.Zero);
    }

    private void SetVisible(bool keepPowerEffects)
    {
        StopAnimations(keepPowerEffects);
        _phase = Phase.Visible;
        _shape.InsertVector2("Size", _target);
        _surface.Opacity = _content.Opacity = 1;
        _surface.Scale = Vector3.One;
        _surface.Properties.InsertVector3("Translation", Vector3.Zero);
        _content.Properties.InsertVector3("Translation", Vector3.Zero);
        Settled?.Invoke();
    }

    private void StopAnimations(bool keepPowerEffects)
    {
        _shape.StopAnimation("Size");
        _surface.StopAnimation(nameof(Visual.Scale));
        foreach (var visual in new[] { _surface, _content })
            foreach (var property in new[] { nameof(Visual.Opacity), "Translation" }) visual.StopAnimation(property);
        ResetBadge(keepPowerEffects);
        if (!keepPowerEffects) _aura?.Stop();
    }

    public void Reset()
    {
        CancelCompletion();
        StopAnimations(keepPowerEffects: false);
        _phase = Phase.Hidden;
        _surface.Opacity = 0;
        _content.Opacity = 1;
        _surface.Scale = _content.Scale = Vector3.One;
        _surface.Properties.InsertVector3("Translation", Vector3.Zero);
        _content.Properties.InsertVector3("Translation", Vector3.Zero);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Reset();
        _disposed = true;
        Settled = null;
        _surface.StopAnimation(nameof(Visual.CenterPoint));
        _aura?.Dispose();
        _shape.Dispose();
        _expand.Dispose();
        _contract.Dispose();
        _opacity.Dispose();
    }
}
