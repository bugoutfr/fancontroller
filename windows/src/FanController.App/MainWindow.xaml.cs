using FanController.App.Localization;
using FanController.App.ViewModels;
using FanController.App.Services;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace FanController.App;

/// <summary>Composes desktop services and owns window, language and tray presentation.</summary>
public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly TrayIcon _tray;
    private bool _quitting;
    private bool _initializingLanguage = true;
    private bool _isFrench;
    private bool _shutdownStarted, _shutdownComplete;
    private bool _firmwarePicking;
    private readonly LanguagePreferences _languagePreferences = new(
        Path.Combine(DesktopServices.DataDirectory, "language.txt"));

    /// <summary>Creates the desktop presentation and its platform service composition.</summary>
    public MainWindow()
    {
        var language = DetectInitialLanguage(out var languageError);
        Localizer.SetLanguage(language);
        InitializeComponent();
        LanguageSelector.SelectedIndex = language == "fr" ? 1 : 0;
        _initializingLanguage = false;
        _isFrench = language == "fr";
        Root.Language = language == "fr" ? "fr-FR" : "en-US";
        Title = Localizer.Get("AppTitle");

        _viewModel = DesktopServices.CreateViewModel(DispatcherQueue);
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
        AppWindow.Closing += async (_, args) =>
        {
            if (_shutdownComplete)
                return;
            args.Cancel = true;
            if (!_quitting && _tray.IsAvailable)
            {
                AppWindow.Hide();
                return;
            }

            if (_shutdownStarted)
                return;
            _shutdownStarted = true;
            try
            {
                await _viewModel.DisposeAsync();
                _tray.Dispose();
                _shutdownComplete = true;
                Close();
            }
            catch (Exception ex)
            {
                _viewModel.ReportError(ex);
                _shutdownStarted = false;
                _quitting = false;
                ShowWindow();
            }
        };
        Closed += (_, _) =>
        {
            _viewModel.PropertyChanged -= ConnectionChanged;
            _viewModel.History.PropertyChanged -= ConnectionChanged;
            _tray.Dispose();
        };
        ApplyLanguage();
        if (languageError is not null)
            _viewModel.ReportError(languageError);
    }

    private string DetectInitialLanguage(out Exception? error)
    {
        error = null;
        try
        {
            if (_languagePreferences.Load() is { } saved)
                return saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex;
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
        if (_initializingLanguage || LanguageSelector.SelectedIndex < 0)
            return;
        var language = LanguageSelector.SelectedIndex == 1 ? "fr" : "en";
        try
        {
            _languagePreferences.Save(language);
            Localizer.SetLanguage(language);
            _isFrench = language == "fr";
            Root.Language = _isFrench ? "fr-FR" : "en-US";
            ApplyLanguage();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _initializingLanguage = true;
            LanguageSelector.SelectedIndex = _isFrench ? 1 : 0;
            _initializingLanguage = false;
            _viewModel.ReportError(ex);
        }
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
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Restore();
        Activate();
    }

    private void Quit()
    {
        _quitting = true;
        Close();
    }

    private void QuitClicked(object sender, RoutedEventArgs args) => Quit();

    private async void FirmwareUpdateClicked(object sender, RoutedEventArgs args)
    {
        if (_firmwarePicking || !_viewModel.CanUpdateFirmware)
            return;
        _firmwarePicking = true;
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".bin");
            var file = await picker.PickSingleFileAsync();
            if (file is not null && !_shutdownStarted && _viewModel.CanUpdateFirmware)
                await _viewModel.UpdateFirmwareAsync(file.Path, ConfirmFirmwareActivationAsync);
        }
        catch (Exception ex) { _viewModel.ReportError(ex); }
        finally { _firmwarePicking = false; }
    }

    private void FirmwareCancelClicked(object sender, RoutedEventArgs args) => _viewModel.CancelFirmwareUpdate();

    private Task<bool> ConfirmFirmwareActivationAsync(CancellationToken ct)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var dialog = new ContentDialog
                {
                    XamlRoot = Root.XamlRoot,
                    RequestedTheme = ElementTheme.Dark,
                    Title = Localizer.Get("Ota.ConfirmTitle", "Firmware verified: restart now?"),
                    Content = Localizer.Get("Ota.ConfirmBody", "The transfer passed integrity and image checks. Restarting briefly interrupts regulation and may interrupt cooling. Unsaved RAM settings will be lost. Only proceed when it is safe."),
                    PrimaryButtonText = Localizer.Get("Ota.Activate", "Activate and restart"),
                    CloseButtonText = Localizer.Get("Ota.Discard", "Discard firmware"),
                    DefaultButton = ContentDialogButton.Close
                };
                using var registration = ct.Register(() => DispatcherQueue.TryEnqueue(() => dialog.Hide()));
                var result = await dialog.ShowAsync();
                ct.ThrowIfCancellationRequested();
                completion.TrySetResult(result == ContentDialogResult.Primary);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { completion.TrySetCanceled(ct); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }))
            completion.TrySetException(new IOException("The firmware confirmation could not be displayed."));
        return completion.Task;
    }
}
