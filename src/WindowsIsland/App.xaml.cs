using System.Diagnostics;
using Microsoft.UI.Xaml;
using WindowsIsland.Services;

namespace WindowsIsland;

public partial class App : Application
{
    private MainWindow? _window;
    private SingleInstance? _instance;
    public App() => InitializeComponent();
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (_window is not null) { _window.OpenSettings(); return; }
        var arguments = args.Arguments + " " + string.Join(" ", Environment.GetCommandLineArgs().Skip(1));
        var verification = arguments.Contains("--verify-local", StringComparison.Ordinal);
        _instance = SingleInstance.TryAcquire("Local\\WindowsIsland.Tray");
        if (_instance is null)
        {
            if (!verification) AppActivation.ShowSettings();
            Exit();
            return;
        }
        var settings = new SettingsStore();
        var showSettings = !settings.Current.SetupCompleted || arguments.Contains("--settings", StringComparison.Ordinal);
        var afterUpdate = arguments.Contains("--after-update", StringComparison.Ordinal);
        var showUpdateResult = afterUpdate || arguments.Contains("--show-update-result", StringComparison.Ordinal);
        if (!verification && afterUpdate && settings.Current.SystemNotifications)
        {
            try
            {
                var appId = await AppInitialization.InitializeAsync(force: true);
                if (!AppInitialization.HasIdentity)
                {
                    RestartRegistered(appId, showSettings: true, showUpdateResult: true);
                    return;
                }
            }
            catch (Exception error) { Trace.WriteLine(error); showSettings = true; }
        }
        if (!verification && settings.Current.SystemNotifications && !AppInitialization.HasIdentity)
        {
            try
            {
                if (AppInitialization.RegisteredAppId() is { } appId)
                {
                    RestartRegistered(appId, showSettings, showUpdateResult);
                    return;
                }
            }
            catch (Exception error)
            {
                Trace.WriteLine(error);
                if (_instance is null) return;
            }
            showSettings = true;
        }
        _window = new MainWindow(settings, verification, showUpdateResult);
        _window.Closed += (_, _) => _instance?.Dispose();
        if (!verification && showSettings) _window.OpenSettings();
        await _window.StartListeningAsync();
    }

    internal void RestartRegistered(string appId, bool showSettings = true, bool showUpdateResult = false)
    {
        _instance?.Dispose();
        try { AppActivation.LaunchRegistered(appId, showSettings, showUpdateResult); }
        catch
        {
            _instance = SingleInstance.TryAcquire("Local\\WindowsIsland.Tray");
            if (_instance is null) CloseApplication();
            throw;
        }
        CloseApplication();
    }

    private void CloseApplication()
    {
        if (_window is null) Exit();
        else _window.ExitApplication();
    }
}
