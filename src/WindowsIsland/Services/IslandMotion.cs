using System.Diagnostics;
using Microsoft.UI.Xaml.Media;

namespace WindowsIsland.Services;

internal sealed class IslandMotion(Action<double> render) : IDisposable
{
    private readonly Stopwatch _clock = new();
    public bool IsRunning { get; private set; }

    public void Start()
    {
        _clock.Restart();
        if (IsRunning) return;
        IsRunning = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, object args)
    {
        var progress = Math.Clamp(_clock.Elapsed.TotalMilliseconds / 280, 0, 1);
        render(progress);
        if (progress >= 1) Stop();
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        CompositionTarget.Rendering -= OnRendering;
        _clock.Stop();
    }

    public void Dispose() => Stop();
}
