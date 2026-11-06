using WindowsIsland.Services;

namespace WindowsIsland;

public sealed partial class MainWindow
{
    private Task? _exitTask;
    private bool _shutdownStarted;

    // Action callbacks cannot return a Task; registered restart awaits the same operation directly.
    internal async void ExitApplication()
    {
        try { await ExitApplicationAsync(); }
        catch (Exception error) { Diagnostics.Capture(DiagnosticArea.Application, error, "Shutdown"); }
    }

    internal Task ExitApplicationAsync()
    {
        if (_exitTask is not null) return _exitTask;
        if (_closed) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _exitTask = completion.Task;
        _ = CompleteExitAsync(completion);
        return completion.Task;
    }

    private async Task CompleteExitAsync(TaskCompletionSource completion)
    {
        try
        {
            if (!_shutdownStarted)
            {
                _shutdownStarted = true;
                PrepareForExit();
            }
            // Keep the final window and its dispatcher alive for lifecycle continuations.
            await ShutdownPluginsCoreAsync();
            _exiting = true;
            Close();
            completion.SetResult();
        }
        catch (Exception error)
        {
            _exiting = false;
            _exitTask = null;
            completion.SetException(error);
        }
    }
}
