namespace WindowsIsland.Services;

internal sealed record UpdateCheckState(bool Busy = false, bool Checked = false,
    GitHubRelease? Candidate = null, string? ErrorKey = null, bool Canceled = false);

internal sealed class UpdateCheckService : IDisposable
{
    private readonly object _gate = new();
    private readonly SettingsStore _store;
    private readonly GitHubReleaseClient? _client;
    private readonly Func<bool, CancellationToken, Task<GitHubRelease?>> _query;
    private readonly TimeSpan _timeout;
    private CancellationTokenSource? _operation;
    private Task _running = Task.CompletedTask;
    private UpdateCheckState _state = new();
    private bool _attempted, _disposed, _includePrereleases;
    public event Action? Changed;
    public UpdateCheckState State { get { lock (_gate) return _state; } }

    public UpdateCheckService(SettingsStore store,
        Func<bool, CancellationToken, Task<GitHubRelease?>>? query = null, TimeSpan? timeout = null)
    {
        _store = store;
        _includePrereleases = store.Current.IncludePrereleaseUpdates;
        _timeout = timeout ?? TimeSpan.FromSeconds(45);
        if (query is null)
        {
            _client = new GitHubReleaseClient();
            _query = (include, token) => _client.FindUpdateAsync(ReleaseVersion.Current, include, token);
        }
        else _query = query;
        store.Changed += OnSettingsChanged;
    }

    public Task StartOnce() => CheckAsync(onlyOnce: true);
    public Task CheckAsync() => CheckAsync(onlyOnce: false);

    private Task CheckAsync(bool onlyOnce)
    {
        Task running;
        lock (_gate)
        {
            if (_disposed || onlyOnce && _attempted || _state.Busy) return _running;
            _attempted = true;
            var operation = new CancellationTokenSource(_timeout);
            _operation = operation;
            _state = new(Busy: true);
            var include = _includePrereleases;
            running = _running = Task.Run(() => RunAsync(include, operation));
        }
        Changed?.Invoke();
        return running;
    }

    private async Task RunAsync(bool include, CancellationTokenSource operation)
    {
        UpdateCheckState result;
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            var candidate = await _query(include, operation.Token).WaitAsync(operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            result = new(Checked: true, Candidate: candidate);
        }
        catch (OperationCanceledException) { result = new(Canceled: true); }
        catch (Exception error)
        {
            Diagnostics.Capture(DiagnosticArea.Updates, error);
            result = new(ErrorKey: Localization.ErrorKey(error, "UpdateFetchError"));
        }
        var changed = false;
        lock (_gate)
        {
            if (!_disposed && ReferenceEquals(_operation, operation))
            {
                _operation = null;
                _state = operation.IsCancellationRequested ? new(Canceled: true) : result;
                changed = true;
            }
            operation.Dispose();
        }
        if (changed) Changed?.Invoke();
    }

    public void Cancel()
    {
        lock (_gate) _operation?.Cancel();
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        lock (_gate)
        {
            if (_disposed || settings.IncludePrereleaseUpdates == _includePrereleases) return;
            _includePrereleases = settings.IncludePrereleaseUpdates;
            _operation?.Cancel();
            _operation = null;
            _state = new();
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _operation?.Cancel();
            _operation = null;
            _state = new();
        }
        _store.Changed -= OnSettingsChanged;
        _client?.Dispose();
    }
}
