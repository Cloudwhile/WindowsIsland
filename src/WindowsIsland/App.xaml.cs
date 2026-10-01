using Microsoft.UI.Xaml;

namespace WindowsIsland;

public partial class App : Application
{
    private MainWindow? _window;
    public App() => InitializeComponent();
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        await _window.StartListeningAsync();
    }
}
