using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WindowsIsland.Services;

namespace WindowsIsland;

internal static class Program
{
    [STAThread]
    private static void Main(string[] arguments)
    {
        if (StartupElevation.TryHandle(arguments, out var result)) { Environment.ExitCode = result; return; }
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }
}
