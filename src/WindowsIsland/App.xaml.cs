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
        if (!verification && settings.Current.SystemNotifications && !AppInitialization.HasIdentity)
        {
            try
            {
                if (AppInitialization.RegisteredAppId() is { } appId)
                {
                    RestartRegistered(appId, showSettings);
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
        _window = new MainWindow(settings, verification);
        _window.Closed += (_, _) => _instance?.Dispose();
        if (!verification && showSettings) _window.OpenSettings();
        await _window.StartListeningAsync();
    }

    internal void RestartRegistered(string appId, bool showSettings = true)
    {
        _instance?.Dispose();
        try { AppActivation.LaunchRegistered(appId, showSettings); }
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
