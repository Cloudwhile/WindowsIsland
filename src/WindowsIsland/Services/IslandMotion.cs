using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;

namespace WindowsIsland.Services;

internal sealed class IslandMotion : IDisposable
{
    private enum Phase { Hidden, Opening, Visible, Closing }
    private const int OpenDuration = 480, CloseDuration = 460, UpdateDuration = 260;
    private readonly Visual _surface, _content;
    private readonly Compositor _compositor;
    private readonly CompositionPropertySet _shape;
    private readonly CubicBezierEasingFunction _expand, _contract;
    private readonly Func<bool> _animationsEnabled;
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
        Func<bool> animationsEnabled, NotificationPosition position = NotificationPosition.TopCenter)
    {
        _surface = ElementCompositionPreview.GetElementVisual(surface);
        _content = ElementCompositionPreview.GetElementVisual(content);
        ElementCompositionPreview.SetIsTranslationEnabled(surface, true);
        ElementCompositionPreview.SetIsTranslationEnabled(content, true);
        _compositor = _surface.Compositor;
        _animationsEnabled = animationsEnabled;
        _inset = (float)inset;
        _shape = _compositor.CreatePropertySet();
        _shape.InsertVector2("Size", Vector2.One);
        _expand = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 0.8f), new Vector2(0.18f, 1));
        _contract = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0), new Vector2(0.2f, 1));
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
    }

    public void Show(Size previous, Size target, bool visible, bool newMessage)
    {
        if (_disposed) return;
        var opening = !visible || _phase == Phase.Hidden;
        var settled = _phase == Phase.Visible;
        CancelCompletion();
        _target = new Vector2((float)target.Width, (float)target.Height);
        if (opening)
        {
            StopAnimations();
            _shape.InsertVector2("Size", LineSize);
            _surface.Opacity = _content.Opacity = 0;
            _surface.Properties.InsertVector3("Translation", LinePosition);
            _content.Properties.InsertVector3("Translation", new Vector3(NotificationPlacement.Outward(_position) * -6, 0));
        }
        else if (settled) _shape.InsertVector2("Size", new Vector2((float)previous.Width, (float)previous.Height));
        if (!_animationsEnabled()) { SetVisible(); return; }
        ScaleSurface();
        _phase = Phase.Opening;
        Begin(() =>
        {
            if (opening)
            {
                AnimateShape(OpenDuration, _expand, 0, (0.14f, LineSize), (0.26f, Shoulder()), (1, _target));
                Animate(_surface, nameof(Visual.Opacity), OpenDuration, _expand, (0.08f, 1), (1, 1));
                Animate(_surface, "Translation", OpenDuration, _expand, (0.14f, LinePosition), (1, Vector3.Zero));
                Animate(_content, nameof(Visual.Opacity), OpenDuration, _expand, (0.35f, 0), (0.90f, 1), (1, 1));
                Animate(_content, "Translation", Vector3.Zero, OpenDuration, _expand);
            }
            else
            {
                // StartingValue also covers an arrival during the previous opening or closing motion.
                AnimateShape(UpdateDuration, _expand, 0, (1, _target));
                Animate(_surface, nameof(Visual.Opacity), UpdateDuration, _expand, (1, 1));
                Animate(_surface, "Translation", Vector3.Zero, UpdateDuration, _expand);
                if (newMessage && settled)
                    Animate(_content, nameof(Visual.Opacity), UpdateDuration, _expand, (0.16f, 0.45f), (1, 1));
                else Animate(_content, nameof(Visual.Opacity), UpdateDuration, _expand, (1, 1));
                Animate(_content, "Translation", Vector3.Zero, UpdateDuration, _expand);
            }
        }, SetVisible);
    }

    public void Hide(Action completed)
    {
        if (_disposed) return;
        var settled = _phase == Phase.Visible;
        CancelCompletion();
        if (!_animationsEnabled()) { Reset(); completed(); return; }
        if (settled) _shape.InsertVector2("Size", _target);
        ScaleSurface();
        _phase = Phase.Closing;
        Begin(() =>
        {
            Animate(_content, nameof(Visual.Opacity), 70, _contract, (1, 0));
            Animate(_content, "Translation", new Vector3(NotificationPlacement.Outward(_position) * 4, 0), 70, _contract);
            AnimateShape(CloseDuration, _contract, 0.16f, (0.60f, Shoulder()), (0.74f, LineSize), (1, LineSize));
            Animate(_surface, "Translation", CloseDuration, _contract, (0.74f, LinePosition), (1, LinePosition));
            Animate(_surface, nameof(Visual.Opacity), CloseDuration, _contract, (0.96f, 1), (1, 0));
        }, completed);
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

    private void SetVisible()
    {
        StopAnimations();
        _phase = Phase.Visible;
        _shape.InsertVector2("Size", _target);
        _surface.Opacity = _content.Opacity = 1;
        _surface.Scale = Vector3.One;
        _surface.Properties.InsertVector3("Translation", Vector3.Zero);
        _content.Properties.InsertVector3("Translation", Vector3.Zero);
        Settled?.Invoke();
    }

    private void StopAnimations()
    {
        _shape.StopAnimation("Size");
        _surface.StopAnimation(nameof(Visual.Scale));
        foreach (var visual in new[] { _surface, _content })
            foreach (var property in new[] { nameof(Visual.Opacity), "Translation" }) visual.StopAnimation(property);
    }

    public void Reset()
    {
        CancelCompletion();
        StopAnimations();
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
        _shape.Dispose();
        _expand.Dispose();
        _contract.Dispose();
    }
}
