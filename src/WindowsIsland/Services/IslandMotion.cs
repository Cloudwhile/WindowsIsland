using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;

namespace WindowsIsland.Services;

internal sealed class IslandMotion : IDisposable
{
    private enum Phase { Hidden, Opening, Visible, Closing }
    private const int OpenDuration = 480, CloseDuration = 360, UpdateDuration = 260;
    private static readonly Vector2 LineSize = new(72, 3);
    private readonly Visual _surface, _content;
    private readonly Compositor _compositor;
    private readonly CompositionRoundedRectangleGeometry _outline;
    private readonly CompositionGeometricClip _clip;
    private readonly CubicBezierEasingFunction _expand, _contract;
    private readonly Func<bool> _animationsEnabled;
    private readonly Vector3 _linePosition;
    private CompositionScopedBatch? _batch;
    private Vector2 _target;
    private Phase _phase;
    private int _generation;
    private bool _disposed;
    public bool IsRunning { get; private set; }
    public event Action? Settled;

    public IslandMotion(FrameworkElement surface, FrameworkElement content, double topInset, double cornerRadius, Func<bool> animationsEnabled)
    {
        _surface = ElementCompositionPreview.GetElementVisual(surface);
        _content = ElementCompositionPreview.GetElementVisual(content);
        ElementCompositionPreview.SetIsTranslationEnabled(surface, true);
        ElementCompositionPreview.SetIsTranslationEnabled(content, true);
        _compositor = _surface.Compositor;
        _animationsEnabled = animationsEnabled;
        _linePosition = new Vector3(0, (float)(2 - topInset), 0);
        _outline = _compositor.CreateRoundedRectangleGeometry();
        _clip = _compositor.CreateGeometricClip(_outline);
        _expand = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 0.8f), new Vector2(0.18f, 1));
        _contract = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0), new Vector2(0.2f, 1));
        using (var center = _compositor.CreateExpressionAnimation("Vector2((container.Size.X - outline.Size.X) * 0.5, 0)"))
        {
            center.SetReferenceParameter("container", _surface);
            center.SetReferenceParameter("outline", _outline);
            _outline.StartAnimation(nameof(CompositionRoundedRectangleGeometry.Offset), center);
        }
        using (var radius = _compositor.CreateExpressionAnimation("Vector2(Min(outline.Size.Y * 0.5, radius), Min(outline.Size.Y * 0.5, radius))"))
        {
            radius.SetReferenceParameter("outline", _outline);
            radius.SetScalarParameter("radius", (float)cornerRadius);
            _outline.StartAnimation(nameof(CompositionRoundedRectangleGeometry.CornerRadius), radius);
        }
        Reset();
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
            _outline.Size = LineSize;
            _surface.Opacity = _content.Opacity = 0;
            _surface.Properties.InsertVector3("Translation", _linePosition);
            _content.Properties.InsertVector3("Translation", new Vector3(0, 6, 0));
        }
        else if (settled) _outline.Size = new Vector2((float)previous.Width, (float)previous.Height);
        _surface.Clip = _clip;
        if (!_animationsEnabled()) { SetVisible(); return; }
        _phase = Phase.Opening;
        Begin(() =>
        {
            if (opening)
            {
                AnimateShape(OpenDuration, _expand, (0.10f, LineSize), (0.22f, Shoulder()), (1, _target));
                Animate(_surface, nameof(Visual.Opacity), OpenDuration, _expand, (0.08f, 1), (1, 1));
                Animate(_surface, "Translation", Vector3.Zero, OpenDuration, _expand);
                Animate(_content, nameof(Visual.Opacity), OpenDuration, _expand, (0.25f, 0), (0.85f, 1), (1, 1));
                Animate(_content, "Translation", Vector3.Zero, OpenDuration, _expand);
            }
            else
            {
                // StartingValue also covers an arrival during the previous opening or closing motion.
                AnimateShape(UpdateDuration, _expand, (1, _target));
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
        if (settled) _outline.Size = _target;
        _surface.Clip = _clip;
        _phase = Phase.Closing;
        Begin(() =>
        {
            Animate(_content, nameof(Visual.Opacity), 90, _contract, (1, 0));
            Animate(_content, "Translation", new Vector3(0, -4, 0), 120, _contract);
            AnimateShape(CloseDuration, _contract, (0.78f, Shoulder()), (0.92f, LineSize), (1, LineSize));
            Animate(_surface, "Translation", _linePosition, CloseDuration, _contract);
            Animate(_surface, nameof(Visual.Opacity), CloseDuration, _contract, (0.91f, 1), (1, 0));
        }, completed);
    }

    private Vector2 Shoulder() => new(Math.Min(144, _target.X), 6);

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

    private void AnimateShape(int milliseconds, CompositionEasingFunction easing, params (float At, Vector2 Value)[] frames)
    {
        using var animation = _compositor.CreateVector2KeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        animation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        foreach (var frame in frames) animation.InsertKeyFrame(frame.At, frame.Value, easing);
        _outline.StartAnimation(nameof(CompositionRoundedRectangleGeometry.Size), animation);
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
    {
        using var animation = _compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        animation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, target, easing);
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
        _outline.Size = _target;
        _surface.Opacity = _content.Opacity = 1;
        _surface.Scale = Vector3.One;
        _surface.Properties.InsertVector3("Translation", Vector3.Zero);
        _content.Properties.InsertVector3("Translation", Vector3.Zero);
        Settled?.Invoke();
        _surface.Clip = null;
    }

    private void StopAnimations()
    {
        _outline.StopAnimation(nameof(CompositionRoundedRectangleGeometry.Size));
        foreach (var visual in new[] { _surface, _content })
            foreach (var property in new[] { nameof(Visual.Opacity), "Translation" }) visual.StopAnimation(property);
    }

    public void Reset()
    {
        CancelCompletion();
        StopAnimations();
        _phase = Phase.Hidden;
        _surface.Clip = null;
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
        _clip.Dispose();
        _outline.Dispose();
        _expand.Dispose();
        _contract.Dispose();
    }
}
