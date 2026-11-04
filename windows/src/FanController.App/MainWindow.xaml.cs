using FanController.App.Localization;
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
    private bool _initializingLanguage = true;
    private bool _isFrench;

    public MainWindow()
    {
        var language = DetectInitialLanguage();
        Localizer.SetLanguage(language);
        InitializeComponent();
        LanguageSelector.SelectedIndex = language == "fr" ? 1 : 0;
        _initializingLanguage = false;
        _isFrench = language == "fr";
        Root.Language = language == "fr" ? "fr-FR" : "en-US";
        Title = Localizer.Get("AppTitle");

        _viewModel = new(DispatcherQueue);
        Root.DataContext = _viewModel;
        Root.Loaded += (_, _) => ApplyLanguage();

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "FanController.ico");
        AppWindow.SetIcon(iconPath);
        AppWindow.Resize(new SizeInt32(1180, 900));
        _tray = new(WinRT.Interop.WindowNative.GetWindowHandle(this), DispatcherQueue, iconPath,
            Path.Combine(AppContext.BaseDirectory, "Assets", "FanController.Disconnected.ico"),
            ShowWindow, () => AppWindow.Hide(), Quit);
        _tray.SetFrench(_isFrench);
        _viewModel.PropertyChanged += ConnectionChanged;
        _viewModel.History.PropertyChanged += ConnectionChanged;
        AppWindow.Closing += (_, args) =>
        {
            if (_quitting) return;
            if (!_tray.IsAvailable)
            {
                _quitting = true;
                return;
            }
            args.Cancel = true;
            AppWindow.Hide();
        };
        Closed += (_, _) =>
        {
            _viewModel.PropertyChanged -= ConnectionChanged;
            _viewModel.History.PropertyChanged -= ConnectionChanged;
            _tray.Dispose();
            _viewModel.Dispose();
        };
        ApplyLanguage();
    }

    private static string LanguagePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanController", "language.txt");

    private static string DetectInitialLanguage()
    {
        if (File.Exists(LanguagePath))
        {
            var saved = File.ReadAllText(LanguagePath).Trim();
            if (saved is "fr" or "en") return saved;
        }
        return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en";
    }

    private void PivotSelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        ApplyLanguage();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ApplyLanguage());
    }

    private void ConnectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.Error))
            ErrorDetailsText.Text = _viewModel.Error;
        if (args.PropertyName == nameof(MainViewModel.Connected))
            _tray.SetConnected(_viewModel.Connected);
    }

    private void LanguageChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs args)
    {
        if (_initializingLanguage || LanguageSelector.SelectedIndex < 0) return;
        var language = LanguageSelector.SelectedIndex == 1 ? "fr" : "en";
        Directory.CreateDirectory(Path.GetDirectoryName(LanguagePath)!);
        File.WriteAllText(LanguagePath, language);
        Localizer.SetLanguage(language);
        _isFrench = language == "fr";
        Root.Language = _isFrench ? "fr-FR" : "en-US";
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        DashboardTabItem.Header = Localizer.Get("DashboardTab.Header", "Dashboard");
        HistoryTabItem.Header = Localizer.Get("HistoryTab.Header", "History");
        SettingsTabItem.Header = Localizer.Get("SettingsTab.Header", "Settings");
        Localizer.Apply(Root);
        Title = Localizer.Get("AppTitle");
        _viewModel.RefreshLocalization();
        _tray.SetFrench(_isFrench);
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
