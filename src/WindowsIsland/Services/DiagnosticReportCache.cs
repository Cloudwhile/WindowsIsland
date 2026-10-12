namespace WindowsIsland.Services;

internal sealed class DiagnosticReportCache(Func<string> generate, TimeProvider? clock = null) : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private Task<string>? _report;
    private CancellationTokenSource? _generation;
    private long _created;
    private bool _disposed;

    public Task<string> GetAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_report is { IsFaulted: false, IsCanceled: false }
                && (!_report.IsCompleted || _clock.GetElapsedTime(_created) < TimeSpan.FromSeconds(60))) return _report;
            _generation?.Dispose();
            _generation = new();
            _created = _clock.GetTimestamp();
            return _report = Task.Run(generate).WaitAsync(_generation.Token);
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _generation?.Cancel();
            _generation?.Dispose();
            _generation = null;
            _report = null;
        }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; Invalidate(); }
    }
}
