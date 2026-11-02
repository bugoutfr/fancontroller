using Microsoft.UI.Xaml;
using System.Globalization;
using Microsoft.Windows.AppLifecycle;

namespace FanController.App;

public partial class App : Application
{
    private MainWindow? _window;
    public App()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
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
        instance.Activated += (_, _) => _window?.DispatcherQueue.TryEnqueue(() => _window.ShowWindow());
        _window = new MainWindow();
        _window.Activate();
    }
}
