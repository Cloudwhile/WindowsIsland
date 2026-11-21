using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WindowsIsland.Plugins;
using WindowsIsland.Services;

namespace WindowsIsland;

internal static class Program
{
    [STAThread]
    private static void Main(string[] arguments)
    {
        if (FileDialogProcess.TryHandle(arguments, out var dialogResult)) { Environment.ExitCode = dialogResult; return; }
        if (StartupElevation.TryHandle(arguments, out var result)) { Environment.ExitCode = result; return; }
        if (InitializationInstaller.TryHandle(arguments, out result)) { Environment.ExitCode = result; return; }
        if (InitializationElevation.TryHandle(arguments, out result)) { Environment.ExitCode = result; return; }
        var verification = arguments.Contains("--verify-local");
        var appDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsIsland");
        Exception? hostDataProtectionError = null;
        if (OperatingSystem.IsWindows())
        {
            if (verification) SandboxAccess.DisableExternalPlugins();
            else
            {
                SandboxAccess.RequireHostDataRootProtection(appDataRoot);
                try { SandboxAccess.ProtectHostDataRoot(appDataRoot); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or PluginException)
                {
                    hostDataProtectionError = error;
                    SandboxAccess.RecordHostDataRootFailure(error);
                }
            }
        }
        var settings = new SettingsStore(verification ? Path.Combine(AppContext.BaseDirectory, "verification-settings.json") : null);
        Diagnostics.Initialize(verification ? Path.Combine(AppContext.BaseDirectory, "verification-diagnostics")
            : Path.Combine(appDataRoot, "Diagnostics"),
            settings.Current.LocalDiagnostics);
        if (hostDataProtectionError is not null)
        {
            Diagnostics.Capture(DiagnosticArea.Application, hostDataProtectionError, "ProtectHostDataRoot");
            Trace.WriteLine(hostDataProtectionError);
        }
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(parameters =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new App();
            });
        }
        catch (Exception error) { Diagnostics.Capture(DiagnosticArea.Application, error, "Startup", fatal: true); throw; }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
            Diagnostics.Shutdown();
        }
    }

    private static void OnUnhandled(object sender, System.UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception error) Diagnostics.Capture(DiagnosticArea.Application, error, "Unhandled", fatal: true);
    }
    private static void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args) =>
        Diagnostics.Capture(DiagnosticArea.Application, args.Exception, "BackgroundTask");
}
