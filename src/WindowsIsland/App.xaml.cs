using System.Diagnostics;
using Microsoft.UI.Xaml;
using WindowsIsland.Services;

namespace WindowsIsland;

public partial class App : Application
{
    private MainWindow? _window;
    private SingleInstance? _instance;
    public App()
    {
        try
        {
            ApplyLanguage(new SettingsStore(PreferencesPath(Environment.GetCommandLineArgs().Contains("--verify-local"))).Current.Language);
            InitializeComponent();
            UnhandledException += (_, error) => RecordVerificationError(error.Exception);
        }
        catch (Exception error) { RecordVerificationError(error); throw; }
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (_window is not null) { _window.OpenSettings(); return; }
        var arguments = args.Arguments + " " + string.Join(" ", Environment.GetCommandLineArgs().Skip(1));
        var verification = arguments.Contains("--verify-local", StringComparison.Ordinal);
        _instance = SingleInstance.TryAcquire(verification ? "Local\\WindowsIsland.Verification." + Environment.ProcessId : "Local\\WindowsIsland.Tray");
        if (_instance is null)
        {
            if (!verification && !arguments.Contains("--startup", StringComparison.Ordinal)) AppActivation.ShowSettings();
            Exit();
            return;
        }
        var settings = new SettingsStore(PreferencesPath(verification));
        ApplyLanguage(settings.Current.Language);
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
        if (showSettings && (!verification || arguments.Contains("--settings", StringComparison.Ordinal))) _window.OpenSettings();
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

    private static string? PreferencesPath(bool verification) => verification
        ? Path.Combine(AppContext.BaseDirectory, "verification-settings.json") : null;

    private static void RecordVerificationError(Exception error)
    {
        if (!Environment.GetCommandLineArgs().Contains("--verify-local")) return;
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "verification-error.log"), error + Environment.NewLine); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void ApplyLanguage(string preference)
    {
        var language = LanguagePreferences.Resolve(preference, Windows.System.UserProfile.GlobalizationPreferences.Languages);
        if (AppInitialization.HasIdentity) Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language;
        Localization.Configure(language);
    }

    private void CloseApplication()
    {
        if (_window is null) Exit();
        else _window.ExitApplication();
    }
}
