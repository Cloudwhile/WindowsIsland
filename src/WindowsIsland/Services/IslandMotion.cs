using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace WindowsIsland.Services;

internal sealed class IslandMotion : IDisposable
{
    private readonly Visual _surface, _content;
    private readonly Compositor _compositor;
    private readonly UISettings _settings = new();
    private readonly bool _forceAnimations;
    private CompositionScopedBatch? _batch;
    private int _generation;
    private bool _disposed;
    public bool IsRunning { get; private set; }
    public event Action? Settled;

    public IslandMotion(FrameworkElement surface, FrameworkElement content, bool forceAnimations = false)
    {
        _surface = ElementCompositionPreview.GetElementVisual(surface);
        _content = ElementCompositionPreview.GetElementVisual(content);
        ElementCompositionPreview.SetIsTranslationEnabled(surface, true);
        ElementCompositionPreview.SetIsTranslationEnabled(content, true);
        _compositor = _surface.Compositor;
        _forceAnimations = forceAnimations;
        Reset();
    }

    public void Show(Size previous, Size target, bool visible, bool newMessage)
    {
        if (_disposed) return;
        CancelCompletion();
        _surface.CenterPoint = new Vector3((float)target.Width / 2, 0, 0);
        if (!visible)
        {
            _surface.Opacity = 0;
            _surface.Scale = new Vector3(0.96f, 0.94f, 1);
            _surface.Properties.InsertVector3("Translation", new Vector3(0, -8, 0));
        }
        else if (Math.Abs(previous.Width - target.Width) > 0.5 || Math.Abs(previous.Height - target.Height) > 0.5)
        {
            _surface.StopAnimation(nameof(Visual.Scale));
            _surface.Scale = new Vector3((float)(previous.Width / target.Width), (float)(previous.Height / target.Height), 1);
        }
        if (!_forceAnimations && !_settings.AnimationsEnabled) { SetVisible(); return; }
        Begin(() =>
        {
            Animate(_surface, nameof(Visual.Scale), Vector3.One, 300);
            Animate(_surface, "Translation", Vector3.Zero, 300);
            Animate(_surface, nameof(Visual.Opacity), 1, 180);
            if (newMessage)
            {
                _content.StopAnimation(nameof(Visual.Opacity));
                _content.StopAnimation("Translation");
                _content.Opacity = 0;
                _content.Properties.InsertVector3("Translation", new Vector3(0, 4, 0));
                Animate(_content, nameof(Visual.Opacity), 1, 180);
                Animate(_content, "Translation", Vector3.Zero, 240);
            }
        }, SetVisible);
    }

    public void Hide(Action completed)
    {
        if (_disposed) return;
        CancelCompletion();
        if (!_forceAnimations && !_settings.AnimationsEnabled) { Reset(); completed(); return; }
        Begin(() =>
        {
            Animate(_surface, nameof(Visual.Opacity), 0, 140);
            Animate(_surface, nameof(Visual.Scale), new Vector3(0.97f, 0.95f, 1), 180);
            Animate(_surface, "Translation", new Vector3(0, -6, 0), 180);
        }, completed);
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

    private CubicBezierEasingFunction Ease() => _compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0, 1));

    private void Animate(Visual visual, string property, float target, int milliseconds)
    {
        using var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        animation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        using var easing = Ease();
        animation.InsertKeyFrame(1, target, easing);
        visual.StartAnimation(property, animation);
    }

    private void Animate(Visual visual, string property, Vector3 target, int milliseconds)
    {
        using var animation = _compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        animation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        using var easing = Ease();
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
        _surface.Opacity = _content.Opacity = 1;
        _surface.Scale = Vector3.One;
        _surface.Properties.InsertVector3("Translation", Vector3.Zero);
        _content.Properties.InsertVector3("Translation", Vector3.Zero);
        Settled?.Invoke();
    }

    private void StopAnimations()
    {
        foreach (var visual in new[] { _surface, _content })
            foreach (var property in new[] { nameof(Visual.Opacity), nameof(Visual.Scale), "Translation" }) visual.StopAnimation(property);
    }

    public void Reset()
    {
        CancelCompletion();
        StopAnimations();
        _surface.Opacity = 0;
        _content.Opacity = 1;
        _surface.Scale = Vector3.One;
        _surface.Properties.InsertVector3("Translation", Vector3.Zero);
        _content.Properties.InsertVector3("Translation", Vector3.Zero);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Reset();
        _disposed = true;
        Settled = null;
    }
}
