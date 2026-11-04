using Microsoft.UI.Xaml;
using System.Globalization;
using Microsoft.Windows.AppLifecycle;

namespace FanController.App;

public partial class App : Application
{
    private MainWindow? _window;
    public App()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        UnhandledException += (_, e) => Diagnostics.Record(new Exception(e.Message, e.Exception));
        InitializeComponent();
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var instance = AppInstance.FindOrRegisterForKey("FanController.Desktop");
        if (!instance.IsCurrent)
        {
            await instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            Exit();
            return;
        }
        instance.Activated += (_, activation) =>
        {
            if (activation.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch &&
                launch.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("--startup"))
                return;
            _window?.DispatcherQueue.TryEnqueue(() => _window.ShowWindow());
        };
        _window = new MainWindow();
        if (Environment.GetCommandLineArgs().Contains("--startup"))
            _window.AppWindow.Hide();
        else
            _window.Activate();
        await _window.InitializeAsync();
    }
}
