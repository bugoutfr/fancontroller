using FanController.App.ViewModels;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Microsoft.UI.Windowing;

namespace FanController.App;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly TrayIcon _tray;
    private bool _quitting;
    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new(DispatcherQueue);
        Root.DataContext = _viewModel;
        Root.Language = "fr-FR";
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "FanController.ico");
        AppWindow.SetIcon(iconPath);
        AppWindow.Resize(new SizeInt32(1180, 900));
        _tray = new(WinRT.Interop.WindowNative.GetWindowHandle(this), DispatcherQueue, iconPath,
            ShowWindow, () => AppWindow.Hide(), Quit);
        AppWindow.Closing += (_, args) =>
        {
            if (_quitting) return;
            args.Cancel = true;
            AppWindow.Hide();
        };
        Closed += (_, _) => { _tray.Dispose(); _viewModel.Dispose(); };
    }

    internal Task InitializeAsync() => _viewModel.InitializeAsync();

    internal void ShowWindow()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
        Activate();
    }

    private void Quit()
    {
        _quitting = true;
        Close();
    }

    private void QuitClicked(object sender, RoutedEventArgs args) => Quit();
}
