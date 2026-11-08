using System.Collections.ObjectModel;
using System.Text.Json;
using FanController.App.Localization;
using FanController.App.Services;
using FanController.Core;

namespace FanController.App.ViewModels;

/// <summary>Presents controller state and draft editing; platform work is delegated to injected services.</summary>
public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IUiDispatcher _dispatcher;
    private readonly ControllerConnection _connection;
    private readonly IStartupSettings _startup;
    private readonly DeviceNamePreferences _deviceNamePreferences;
    private readonly Func<IBleScanner> _scannerFactory;
    private readonly Action<Exception> _log;
    private readonly CancellationTokenSource _applicationLifetime = new();
    private bool _initialized, _disposed;
    private Task _initialization = Task.CompletedTask;
    private Task? _shutdown;
    private bool _launchAtStartup;
    private CancellationTokenSource? _firmwareCts;
    private Task<bool>? _firmwareUpdate;
    private bool _firmwareUpdating;
    private double _firmwareProgress;
    private TimeSpan _firmwareElapsed;
    private double _firmwareRate;
    private uint _firmwareAcknowledgedBytes;
    private readonly System.Diagnostics.Stopwatch _firmwareClock = new();

    /// <summary>Localized binary upload duration and mean acknowledged throughput.</summary>
    public string FirmwareTransferDetails => Localizer.Format("Ota.TransferDetails",
        "Upload: {0:0.0} s • mean throughput: {1:0.00} KiB/s",
        _firmwareElapsed.TotalSeconds, _firmwareRate);

    private void RefreshFirmwareTransfer(TimeSpan elapsed)
    {
        _firmwareElapsed = elapsed;
        _firmwareRate = elapsed.TotalSeconds > 0
            ? _firmwareAcknowledgedBytes / 1024.0 / elapsed.TotalSeconds : 0;
        Changed(nameof(FirmwareTransferDetails));
    }

    private async Task RefreshUploadTimerAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                if (_firmwareClock.IsRunning)
                    RefreshFirmwareTransfer(_firmwareClock.Elapsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    private CancellationTokenSource? _tachCts;
    private Task? _tachCapture;
    private bool _tachCapturing;
    private IReadOnlyList<TachWindow> _tachWindows = [];
    private double _tachSeconds;
    private int _tachDuration = 30;
    private string _tachMessageKey = "Tach.Initial";
    private string _tachMessageFallback = "Requires diagnostic firmware. Capture at stable fixed PWM; normal readings pause during capture.";

    /// <summary>Whether this firmware advertises Bluetooth TACH diagnostics.</summary>
    public bool TachSupported => _lastTelemetry?.TachDiagnosticsSupported == true;

    /// <summary>Whether diagnostic acquisition is active.</summary>
    public bool TachCapturing
    {
        get => _tachCapturing;
        private set
        {
            Set(ref _tachCapturing, value);
            RefreshCommands();
            Changed(nameof(CanExportTach));
        }
    }

    /// <summary>Whether a new capture may start on the current firmware.</summary>
    public bool CanCaptureTach => Ready() && TachSupported && !TachCapturing;

    /// <summary>Whether a complete capture can be exported, including after disconnection.</summary>
    public bool CanExportTach => !_disposed && !TachCapturing && _tachWindows.Count != 0;

    /// <summary>Number of seconds represented by the captured windows.</summary>
    public double TachSeconds
    {
        get => _tachSeconds;
        private set => Set(ref _tachSeconds, value);
    }

    /// <summary>Requested duration used by the acquisition progress indicator.</summary>
    public int TachDuration
    {
        get => _tachDuration;
        private set => Set(ref _tachDuration, value);
    }

    /// <summary>Localized capture outcome or instructions.</summary>
    public string TachMessage => Localizer.Get(_tachMessageKey, _tachMessageFallback);

    private void SetTachMessage(string key, string fallback)
    {
        _tachMessageKey = key;
        _tachMessageFallback = fallback;
        Changed(nameof(TachMessage));
    }

    /// <summary>Starts a Bluetooth capture owned and awaited by this view model.</summary>
    /// <param name="seconds">Either 30 or 60 seconds.</param>
    /// <returns>Completion after acquisition or an explicitly reported failure.</returns>
    public Task CaptureTachAsync(int seconds)
    {
        if (!CanCaptureTach)
            throw new InvalidOperationException("TACH diagnostics are unavailable or another operation is active.");
        if (seconds is not (30 or 60))
            throw new ArgumentOutOfRangeException(nameof(seconds));
        return _tachCapture = CaptureTachCoreAsync(seconds);
    }

    private async Task CaptureTachCoreAsync(int seconds)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_applicationLifetime.Token);
        _tachCts = cancellation;
        _tachWindows = [];
        TachSeconds = 0;
        TachDuration = seconds;
        TachCapturing = true;
        Busy = true;
        Error = "";
        SetTachMessage("Tach.Capturing", "Capturing all four fans over Bluetooth. Normal readings are paused.");
        try
        {
            var progress = new Progress<double>(value => Dispatch(() =>
            {
                if (_tachCts == cancellation)
                    TachSeconds = value;
            }));
            _tachWindows = await _connection.CaptureTachAsync(seconds, progress, cancellation.Token);
            TachSeconds = seconds;
            SetTachMessage("Tach.Complete", "Capture complete. Export CSV to save and inspect the measurements.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetTachMessage("Tach.Canceled", "Capture canceled; no partial capture is exported.");
        }
        catch (Exception ex)
        {
            SetTachMessage("Tach.Failed", "Capture failed; no partial capture is exported. Check the connection and firmware.");
            ReportError(ex);
        }
        finally
        {
            _tachCts = null;
            TachCapturing = false;
            Busy = false;
        }
    }

    /// <summary>Cancels acquisition; an in-flight BLE cancellation can require reconnection.</summary>
    public void CancelTachCapture() => _tachCts?.Cancel();

    /// <summary>Writes the last complete capture without replacing it on export failure.</summary>
    /// <param name="path">Destination CSV chosen by the user.</param>
    /// <returns>Completion after the file has been written.</returns>
    public async Task ExportTachAsync(string path)
    {
        if (!CanExportTach)
            throw new InvalidOperationException("No complete TACH capture is available.");
        await File.WriteAllTextAsync(path, TachDiagnostics.ToCsv(_tachWindows), _applicationLifetime.Token);
    }
    private string _firmwareMessageKey = "Ota.Initial", _firmwareMessageFallback = "OTA requires firmware 1.2.0 or later, initially installed over USB.";

    /// <summary>Whether firmware loading, transfer, confirmation or restart verification is active.</summary>
    public bool FirmwareUpdating
    {
        get => _firmwareUpdating;
        private set
        {
            Set(ref _firmwareUpdating, value);
            Changed(nameof(CanUpdateFirmware));
        }
    }

    /// <summary>Whether a new firmware transfer may be started.</summary>
    public bool CanUpdateFirmware => Ready() && !FirmwareUpdating && !TachCapturing && _connection.FirmwareUpdateSupported;

    /// <summary>Percentage of selected firmware bytes acknowledged in flash.</summary>
    public double FirmwareProgress
    {
        get => _firmwareProgress;
        private set => Set(ref _firmwareProgress, value);
    }

    /// <summary>Localized firmware operation status, separate from configuration status.</summary>
    public string FirmwareMessage => Localizer.Get(_firmwareMessageKey, _firmwareMessageFallback);

    private void SetFirmwareMessage(string key, string fallback)
    {
        _firmwareMessageKey = key;
        _firmwareMessageFallback = fallback;
        Changed(nameof(FirmwareMessage));
    }

    /// <summary>Starts an owned OTA operation; shutdown cancels and awaits it.</summary>
    /// <param name="path">Local .bin application file.</param>
    /// <param name="confirmActivation">Cancelable presentation confirmation after firmware validation.</param>
    /// <returns>True after verification of the running image; false when activation was declined.</returns>
    public Task<bool> UpdateFirmwareAsync(string path, Func<CancellationToken, Task<bool>> confirmActivation)
    {
        if (!CanUpdateFirmware)
            throw new InvalidOperationException("Firmware update is unavailable or another operation is active.");
        return _firmwareUpdate = UpdateFirmwareCoreAsync(path, confirmActivation);
    }

    private async Task<bool> UpdateFirmwareCoreAsync(string path, Func<CancellationToken, Task<bool>> confirmActivation)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_applicationLifetime.Token);
        _firmwareCts = cancellation;
        FirmwareUpdating = true;
        Busy = true;
        FirmwareProgress = 0;
        _firmwareClock.Reset();
        _firmwareAcknowledgedBytes = 0;
        RefreshFirmwareTransfer(TimeSpan.Zero);
        using var timerLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var timerTask = RefreshUploadTimerAsync(timerLifetime.Token);
        Error = "";
        SetFirmwareMessage("Ota.Transferring", "Transferring firmware; polling is suspended. Do not remove power.");
        try
        {
            var progress = new Progress<double>(value => FirmwareProgress = value);
            var transferProgress = new Progress<FirmwareTransferProgress>(value =>
            {
                if (_firmwareCts != cancellation)
                    return;
                if (value.AcknowledgedBytes == 0)
                    _firmwareClock.Restart();
                _firmwareAcknowledgedBytes = value.AcknowledgedBytes;
                if (value.AcknowledgedBytes == value.TotalBytes)
                    _firmwareClock.Stop();
                RefreshFirmwareTransfer(value.Elapsed);
            });
            var activated = await _connection.UpdateFirmwareAsync(path, progress, async ct =>
            {
                Dispatch(() => SetFirmwareMessage("Ota.Validated", "Firmware validated; waiting for activation confirmation."));
                var activate = await confirmActivation(ct);
                if (activate)
                    Dispatch(() => SetFirmwareMessage("Ota.Restarting", "Activation requested; reconnecting to verify the running firmware."));
                return activate;
            }, cancellation.Token, transferProgress);
            SetFirmwareMessage(activated ? "Ota.Success" : "Ota.Discarded",
                activated ? "Restart complete: the running firmware image was verified." : "Firmware discarded without activation or restart.");
            return activated;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetFirmwareMessage("Ota.Canceled", "Update canceled. An interrupted transfer is never replayed.");
            return false;
        }
        catch (Exception ex)
        {
            SetFirmwareMessage("Ota.Failed", "Update failed: the running image has not been verified.");
            ReportError(ex);
            return false;
        }
        finally
        {
            if (_firmwareClock.IsRunning)
            {
                _firmwareClock.Stop();
                RefreshFirmwareTransfer(_firmwareClock.Elapsed);
            }
            timerLifetime.Cancel();
            await timerTask;
            _firmwareCts = null;
            FirmwareUpdating = false;
            Busy = false;
        }
    }

    /// <summary>Cancels a firmware operation without replaying any interrupted write.</summary>
    public void CancelFirmwareUpdate() => _firmwareCts?.Cancel();

    /// <summary>Whether this executable launches minimized at Windows sign-in.</summary>
    public bool LaunchAtStartup
    {
        get => _launchAtStartup;
        set
        {
            if (_launchAtStartup == value)
                return;
            try
            {
                _startup.SetEnabled(value);
                Set(ref _launchAtStartup, value);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                _log(ex);
                Error = Localizer.Format("Error.WindowsStartup", "Windows startup: {0}", ex.Message);
                Changed(nameof(LaunchAtStartup));
            }
        }
    }
    private bool _showConnection = true;
    private bool _connecting;

    /// <summary>Whether discovery and manual pairing controls should be shown.</summary>
    public bool ConnectionVisible => _showConnection;

    /// <summary>Whether a current or remembered controller can be disconnected or paused.</summary>
    public bool DisconnectVisible => DisconnectCommand.CanExecute(null);

    /// <summary>The localized disconnect or pause action label.</summary>
    public string DisconnectLabel => Localizer.Get(Connected ? "Action.Disconnect" : "Action.PauseReconnect", Connected ? "Disconnect" : "Pause reconnection");

    /// <summary>The localized connection stage heading.</summary>
    public string ConnectionHeading => Localizer.Get(Connected ? "Connection.HeadingConnected" : _connecting ? "Connection.HeadingConnecting" : "Connection.HeadingDisconnected",
        Connected ? "Connected" : _connecting ? "Connecting…" : "Disconnected");
    private void SetConnecting(bool value)
    {
        _connecting = value;
        Changed(nameof(ConnectionHeading));
    }
    private void ShowConnection(bool value)
    {
        _showConnection = value;
        Changed(nameof(ConnectionVisible));
    }
    private IBleScanner? _scanner;
    private CancellationTokenSource? _scanCts;
    private RemoteConfiguration? _remote;
    private bool _busy, _scanning, _connected, _localDirty;
    private bool _remoteDirty;
    private string _remoteStorage = "";

    /// <summary>Explains the draft, remote RAM and persistence state without implying an unverified save.</summary>
    public string SettingsSummary => !Connected ? Localizer.Get("Settings.NotRefreshed", "Configuration not refreshed: connect the controller.") :
        _remoteStorage is "invalid" or "unavailable" or "write_error" or "verify_error"
            ? Localizer.Format("Settings.SaveProblem", "ESP32 save problem: {0}.", TranslateStorage(_remoteStorage)) :
        _localDirty ? Localizer.Get("Settings.ChangesToApply", "Changes need to be applied.") :
        _remoteDirty ? Localizer.Get("Settings.SaveAfterApply", "Settings applied. Save them to keep them after restart.") :
        Localizer.Get("Settings.Loaded", "Configuration loaded from the ESP32.");
    private string _messageKey = "Connection.SearchPrompt", _messageFallback = "Search for a controller to connect.";
    private object?[] _messageArgs = [];
    private string _error = "", _validation = "", _storage = "Remote state unknown.";
    private BleDeviceDisplay? _selectedDevice;
    private ulong _connectedAddress;
    private string _controllerName = Localizer.Get("Connection.ControllerMissing", "No controller connected");

    /// <summary>The controller name read during discovery.</summary>
    public string ControllerName
    {
        get => _controllerName; private set => Set(ref _controllerName, value);
    }

    /// <summary>The current controller's formatted Bluetooth address.</summary>
    public string ControllerIdentifier => Localizer.Format("Connection.Address", "Bluetooth address: {0}",
        _connectedAddress == 0 ? "—" : string.Join(":", _connectedAddress.ToString("X12").Chunk(2).Select(chars => new string(chars))));
    private int? _bleRssiDbm;
    private bool _rssiSupported;

    /// <summary>The RSSI received by the ESP32, or an explicit unavailable state.</summary>
    public string LinkQuality => !Connected ? Localizer.Get("Connection.LinkInactive", "Link inactive • quality unavailable") :
        _bleRssiDbm is { } rssi
            ? Localizer.Format("Connection.Rssi", "Signal received by ESP32: {0} dBm", rssi)
            : _rssiSupported ? Localizer.Get("Connection.RssiTemporaryUnavailable", "Radio signal temporarily unavailable") :
                Localizer.Get("Connection.RssiFirmwareUnavailable", "Radio signal unavailable • firmware 1.1.3 or later required");
    private string _configurationMessageKey = "", _configurationMessageFallback = "";

    /// <summary>The result of a configuration action, translated in the current language.</summary>
    public string ConfigurationMessage => string.IsNullOrEmpty(_configurationMessageKey) ? "" :
        Localizer.Get(_configurationMessageKey, _configurationMessageFallback);
    private bool _curveEditing;

    /// <summary>Whether curve editing is requested; disconnected or busy state still prevents changes.</summary>
    public bool CurveEditing
    {
        get => _curveEditing;
        set
        {
            if (!Set(ref _curveEditing, value))
                return;
            Changed(nameof(CurveEditorVisible));
            RefreshCommands();
        }
    }
    /// <summary>Whether the coordinate text editor is visible.</summary>
    public bool CurveEditorVisible => CurveEditing;

    private string? _firmwareVersion;

    /// <summary>The received firmware version or disconnected label.</summary>
    public string Firmware => _firmwareVersion is null
        ? Localizer.Get("Firmware.Disconnected", "Firmware: disconnected")
        : Localizer.Format("Firmware.Version", "Firmware {0}", _firmwareVersion);
    /// <summary>The owned presentation of local measurement history.</summary>
    public HistoryViewModel History
    {
        get;
    }

    /// <summary>The stable list of discovered controllers.</summary>
    public ObservableCollection<BleDeviceDisplay> Devices { get; } = [];

    /// <summary>The two editable draft profiles.</summary>
    public ObservableCollection<ProfileEditor> Profiles { get; } = [];

    /// <summary>The four editable draft fan modes.</summary>
    public ObservableCollection<FanEditor> Fans { get; } = [];

    /// <summary>The two stable sensor telemetry cards.</summary>
    public ObservableCollection<SensorDisplay> Sensors { get; } = [];

    /// <summary>The four stable fan telemetry cards.</summary>
    public ObservableCollection<FanDisplay> FanReadings { get; } = [];

    /// <summary>The controller selected for an explicit connection.</summary>
    public BleDeviceDisplay? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            Set(ref _selectedDevice, value);
            RefreshCommands();
        }
    }
    /// <summary>Whether connection or a user operation is in progress.</summary>
    public bool Busy
    {
        get => _busy;
        private set
        {
            Set(ref _busy, value);
            Changed(nameof(CanEdit));
            RefreshCommands();
        }
    }

    /// <summary>Whether the session has completed its initial configuration and status read.</summary>
    public bool Connected
    {
        get => _connected;
        private set
        {
            Set(ref _connected, value);
            Changed(nameof(ConnectionLabel));
            Changed(nameof(LinkQuality));
            Changed(nameof(CanEdit));
            Changed(nameof(SettingsSummary));
            Changed(nameof(SettingsStatus));
            RefreshCommands();
        }
    }

    /// <summary>Whether controls may edit the connected draft.</summary>
    public bool CanEdit => Connected && !Busy && !FirmwareUpdating && !TachCapturing;

    /// <summary>The connection label warning that retained offline readings are stale.</summary>
    public string ConnectionLabel => Localizer.Get(Connected ? "Connection.Connected" : "Connection.Disconnected",
        Connected ? "Connected • local BLE" : "Disconnected • readings are stale");
    /// <summary>The current localized connection operation message.</summary>
    public string Message => Localizer.Format(_messageKey, _messageFallback, _messageArgs);
    private void SetMessage(string key, string fallback, params object?[] args)
    {
        _messageKey = key;
        _messageFallback = fallback;
        _messageArgs = args;
        Changed(nameof(Message));
    }
    private void SetConfigurationMessage(string key, string fallback)
    {
        _configurationMessageKey = key;
        _configurationMessageFallback = fallback;
        Changed(nameof(ConfigurationMessage));
        Changed(nameof(SettingsStatus));
    }
    private void ClearConfigurationMessage()
    {
        _configurationMessageKey = "";
        _configurationMessageFallback = "";
        Changed(nameof(ConfigurationMessage));
        Changed(nameof(SettingsStatus));
    }
    /// <summary>The latest actionable failure shown in the error banner.</summary>
    public string Error
    {
        get => _error; private set
        {
            Set(ref _error, value);
            Changed(nameof(HasError));
        }
    }

    /// <summary>Whether the error banner should be visible.</summary>
    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>The current draft validation error, if any.</summary>
    public string ValidationMessage
    {
        get => _validation; private set => Set(ref _validation, value);
    }

    /// <summary>The action result or current summary displayed beside configuration commands.</summary>
    public string SettingsStatus => string.IsNullOrEmpty(ConfigurationMessage) ? SettingsSummary : ConfigurationMessage;

    /// <summary>The latest reported remote RAM and NVS storage state.</summary>
    public string Storage
    {
        get => _storage; private set => Set(ref _storage, value);
    }

    /// <summary>Whether the local draft matches the last configuration actually read.</summary>
    public string LocalState => Profiles.Count == 0 ? Localizer.Get("Settings.NoConfiguration", "No configuration has been read.") :
        _localDirty ? Localizer.Get("Settings.LocalChanges", "Local changes NOT applied") :
        Localizer.Get("Settings.DraftMatches", "Draft matches the last configuration read");
    private string _lastUpdate = Localizer.Get("State.NoData", "No readings received.");
    private string _deviceNamesStatus = Localizer.Get("DeviceNames.Saved", "Names are saved on this PC only.");

    /// <summary>Explains the local-only status of customized device names.</summary>
    public string DeviceNamesStatus
    {
        get => _deviceNamesStatus;
        private set => Set(ref _deviceNamesStatus, value);
    }

    /// <summary>The timestamp and uptime of the latest displayed measurement.</summary>
    public string LastUpdate
    {
        get => _lastUpdate; private set => Set(ref _lastUpdate, value);
    }
    private Telemetry? _lastTelemetry;
    /// <summary>Searches for controllers and pauses automatic reconnection.</summary>
    public AsyncCommand ScanCommand
    {
        get;
    }

    /// <summary>Connects to the selected controller, permitting explicit pairing.</summary>
    public AsyncCommand ConnectCommand
    {
        get;
    }

    /// <summary>Disconnects and pauses retries for the remainder of this session.</summary>
    public AsyncCommand DisconnectCommand
    {
        get;
    }

    /// <summary>Rereads actual controller settings without replacing the local draft.</summary>
    public AsyncCommand ReadCommand
    {
        get;
    }

    /// <summary>Discards draft changes without writing over BLE.</summary>
    public AsyncCommand AdoptCommand
    {
        get;
    }

    /// <summary>Applies the validated draft to remote RAM without saving to NVS.</summary>
    public AsyncCommand ApplyCommand
    {
        get;
    }

    /// <summary>Saves remote RAM settings separately from applying a draft.</summary>
    public AsyncCommand SaveCommand
    {
        get;
    }

    /// <summary>Loads factory defaults into RAM and replaces the local draft after rereading.</summary>
    public AsyncCommand DefaultsCommand
    {
        get;
    }
    private AsyncCommand[] Commands => [ScanCommand, ConnectCommand, DisconnectCommand, ReadCommand,
        AdoptCommand, ApplyCommand, SaveCommand, DefaultsCommand];

    /// <summary>Refreshes presentation text and validation without changing the draft or sending BLE commands.</summary>
    public void RefreshLocalization()
    {
        Changed(nameof(DisconnectLabel));
        Changed(nameof(ConnectionHeading));
        Changed(nameof(SettingsSummary));
        Changed(nameof(SettingsStatus));
        Changed(nameof(ControllerIdentifier));
        Changed(nameof(LinkQuality));
        Changed(nameof(ConfigurationMessage));
        Changed(nameof(Firmware));
        Changed(nameof(FirmwareMessage));
        Changed(nameof(FirmwareTransferDetails));
        Changed(nameof(TachMessage));
        Changed(nameof(ConnectionLabel));
        Changed(nameof(Message));
        Changed(nameof(LocalState));
        foreach (var sensor in Sensors)
            sensor.RefreshLocalization();
        foreach (var fan in FanReadings)
            fan.RefreshLocalization();
        if (_lastTelemetry is not null)
            UpdateTelemetry(_lastTelemetry);
        else
        {
            var waiting = Localizer.Get("State.Waiting", "Waiting");
            var noData = Localizer.Get("State.NoData", "No readings received.");
            LastUpdate = noData;
            foreach (var sensor in Sensors)
                sensor.SetWaiting(waiting, noData);
            foreach (var fan in FanReadings)
                fan.SetWaiting(waiting, noData);
        }
        foreach (var profile in Profiles)
            profile.RefreshLocalization();
        foreach (var fan in Fans)
            fan.RefreshLocalization();
        History.RefreshLocalization();
        if (!string.IsNullOrEmpty(ValidationMessage))
            DraftChanged();
    }

    /// <summary>Creates presentation commands using externally composed connection and platform services.</summary>
    public MainViewModel(
        ControllerConnection connection, HistoryViewModel history, IUiDispatcher dispatcher,
        IStartupSettings startup, Func<IBleScanner> scannerFactory, Action<Exception> log,
        DeviceNamePreferences deviceNamePreferences)
    {
        _dispatcher = dispatcher;
        _connection = connection;
        _startup = startup;
        _deviceNamePreferences = deviceNamePreferences;
        _scannerFactory = scannerFactory;
        _log = log;
        History = history;

        _connection.Changed += ConnectionChanged;
        _connection.FirmwareVerified += FirmwareVerified;
        _connection.TelemetryReceived += TelemetryReceived;
        _connection.HistoryWritten += HistoryWritten;
        _connection.HistoryWriteFailed += HistoryWriteFailed;
        _connection.PreferenceFailed += PreferenceFailed;

        var deviceNames = ReadDeviceNames();
        for (var sensor = 1; sensor <= 2; sensor++)
        {
            var display = new SensorDisplay(sensor, Localizer.Format("Sensor.Title", "Probe {0}", sensor))
            {
                CustomName = deviceNames.Sensors[sensor - 1]
            };
            display.PropertyChanged += DeviceNameChanged;
            Sensors.Add(display);
        }
        for (var fan = 1; fan <= 4; fan++)
        {
            var display = new FanDisplay(fan, Localizer.Format("Fan.Title", "Fan {0} • probe {1}", fan, (fan - 1) / 2 + 1))
            {
                CustomName = deviceNames.Fans[fan - 1]
            };
            display.PropertyChanged += DeviceNameChanged;
            FanReadings.Add(display);
        }
        UpdateDeviceNameViews();

        ScanCommand = Command(ScanAsync, () => !Busy && !Connected && !_scanning);
        ConnectCommand = Command(ConnectAsync, () => !Busy && !Connected && SelectedDevice is not null);
        DisconnectCommand = Command(DisconnectAsync, () => !_disposed && _connection.CanDisconnect);

        ReadCommand = Command(() => Operation(async () =>
        {
            AcceptRemote(await _connection.SynchronizeAsync(), replaceDraft: false);
            SetConfigurationMessage("Settings.RereadDraftKept", "Configuration reread; draft retained. No changes were reapplied.");
        }), Ready);

        AdoptCommand = Command(() =>
        {
            ReplaceDraft(_remote!.Configuration);
            SetConfigurationMessage("Settings.DraftDiscarded", "Changes discarded without writing to the ESP32.");
            return Task.CompletedTask;
        }, () => !Busy && _remote is not null && _localDirty);

        ApplyCommand = Command(() => Operation(async () =>
        {
            var draft = ReadDraft();
            AcceptRemote(await _connection.ApplyAsync(draft), replaceDraft: true);
            SetConfigurationMessage("Settings.AppliedRam", "Configuration applied to RAM. Save to the ESP32 to keep it after restart.");
        }), () => Ready() && _localDirty && string.IsNullOrEmpty(ValidationMessage));

        SaveCommand = Command(() => Operation(async () =>
        {
            AcceptRemote(await _connection.SaveAsync(), replaceDraft: false);
            SetConfigurationMessage("Settings.Saved", "Configuration saved to and reread from the ESP32.");
        }), () => Ready() && !_localDirty);

        DefaultsCommand = Command(() => Operation(async () =>
        {
            AcceptRemote(await _connection.DefaultsAsync(), replaceDraft: true);
            SetConfigurationMessage("Settings.DefaultsRam", "Factory defaults applied to RAM. Saving to the ESP32 is a separate step.");
        }), Ready);
    }

    private AsyncCommand Command(Func<Task> action, Func<bool> enabled) => new(action, () => !_disposed && !FirmwareUpdating && !TachCapturing && enabled(), ex =>
    {
        if (ex is OperationCanceledException && (_disposed || _connection.ReconnectionPaused))
            return;
        _log(ex);
        if (!_disposed)
            Error = ErrorMessages.Details(ex);
    });

    private bool Ready() => Connected && !Busy && !FirmwareUpdating && !TachCapturing;

    private void RefreshCommands()
    {
        foreach (var command in Commands)
            command?.Refresh();
        foreach (var profile in Profiles)
            profile.IsEditing = CurveEditing && CanEdit;
        Changed(nameof(DisconnectVisible));
        Changed(nameof(DisconnectLabel));
        Changed(nameof(ConnectionHeading));
        Changed(nameof(CanUpdateFirmware));
        Changed(nameof(CanCaptureTach));
        Changed(nameof(CanEdit));
    }

    private void Dispatch(Action action)
    {
        _dispatcher.Post(() =>
        {
            if (!_disposed)
                action();
        });
    }

    private async Task Operation(Func<Task> action)
    {
        Busy = true;
        try
        {
            await action();
        }
        finally { Busy = false; }
    }

    private async Task ScanAsync()
    {
        await _connection.DisconnectAsync();
        _applicationLifetime.Token.ThrowIfCancellationRequested();
        using var scanner = _scannerFactory();
        using var scanLifetime = CancellationTokenSource.CreateLinkedTokenSource(_applicationLifetime.Token);
        _scanner = scanner;
        _scanCts = scanLifetime;

        _scanning = true;
        RefreshCommands();
        Devices.Clear();
        SelectedDevice = null;
        SetMessage("Connection.Searching", "Searching for controllers…");
        scanner.Found += candidate => Dispatch(() =>
        {
            if (_scanner != scanner)
                return;
            var old = Devices.FirstOrDefault(d => d.Address == candidate.Address);
            if (old is null)
                Devices.Add(new(candidate));
            else
                old.Update(candidate);
        });
        scanner.Failed += text => Dispatch(() =>
        {
            _log(new IOException(text));
            Error = text;
        });
        try
        {
            scanner.Start();
            await Task.Delay(TimeSpan.FromSeconds(15), scanLifetime.Token);
            SetMessage(Devices.Count == 0 ? "Error.NoController" : "Connection.SearchComplete",
                Devices.Count == 0 ? "No controller found. Check power, Bluetooth, and range." : "Search complete.");
        }
        catch (OperationCanceledException) when (scanLifetime.IsCancellationRequested) { }
        finally
        {
            if (_scanner == scanner)
                _scanner = null;
            if (_scanCts == scanLifetime)
                _scanCts = null;
            _scanning = false;
            RefreshCommands();
        }
    }

    private async Task ConnectAsync()
    {
        var address = SelectedDevice!.Address;
        _scanCts?.Cancel();
        await ScanCommand.ExecutionTask;
        _applicationLifetime.Token.ThrowIfCancellationRequested();
        await Operation(() => _connection.ConnectAsync(address));
    }

    /// <summary>Initializes local history and startup settings, then enables remembered-device reconnection.</summary>
    public Task InitializeAsync()
    {
        if (_initialized)
            return _initialization;
        _initialized = true;
        return _initialization = InitializeCoreAsync();
    }

    private async Task InitializeCoreAsync()
    {
        try
        {
            Set(ref _launchAtStartup, _startup.Enabled(), nameof(LaunchAtStartup));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log(ex);
            Error = Localizer.Format("Error.WindowsStartup", "Could not read Windows startup setting: {0}", ex.Message);
        }
        await History.InitializeAsync();
        if (!_disposed)
            _connection.Start();
    }

    private void ConnectionChanged(ConnectionUpdate update) => Dispatch(() =>
    {
        switch (update.Stage)
        {
            case ConnectionStage.Connecting:
                _connectedAddress = update.Address;
                ControllerName = Localizer.Get("Connection.ReadingName", "Reading controller name…");
                Changed(nameof(ControllerIdentifier));
                History.SelectDevice(update.Address);
                Connected = false;
                Busy = true;
                SetConnecting(true);
                SetMessage(update.Automatic ? "Connection.AutoConnecting" : "Connection.Connecting",
                    update.Automatic ? "Reconnecting to the last controller…" : "Connecting to controller…");
                break;

            case ConnectionStage.Reading:
                ControllerName = string.IsNullOrWhiteSpace(update.Name)
                    ? Localizer.Get("Connection.NameUnavailable", "Controller name unavailable") : update.Name;
                SetMessage("Connection.ReadingSettings", "Reading settings…");
                break;

            case ConnectionStage.Connected:
                if (update.InitialState is { } state)
                    AcceptRemote(state, replaceDraft: Profiles.Count == 0);
                Error = "";
                Connected = true;
                Busy = false;
                SetConnecting(false);
                ShowConnection(false);
                SetMessage("Connection.Established", "Encrypted Bluetooth link established.");
                break;

            case ConnectionStage.Disconnected:
                Connected = false;
                Busy = false;
                SetConnecting(false);
                ShowConnection(true);
                _bleRssiDbm = null;
                _rssiSupported = false;
                if (update.Error is { } error && !_connection.ReconnectionPaused)
                {
                    ReportError(error);
                    Storage = Localizer.Get("Connection.RemoteStateUncertain", "Remote state is uncertain; reconnect and reread it.");
                    SetMessage("Connection.LinkInterrupted", "Link interrupted.");
                    SetConfigurationMessage("Settings.InterruptedDraft", "Draft retained. An interrupted operation may have been partly applied; check the configuration after reconnecting.");
                }
                break;

            case ConnectionStage.Unpaired:
                Busy = false;
                SetConnecting(false);
                ShowConnection(true);
                SetMessage("Connection.ManualRequired", "The last controller is no longer paired in Windows. Manual connection required.");
                break;

            case ConnectionStage.Retrying:
                Busy = false;
                SetConnecting(false);
                ShowConnection(true);
                if (update.Error is { } retryError)
                    ReportError(retryError);
                SetMessage("Error.Retrying", "Automatic reconnection • retrying in {0:0} s.", update.RetryDelay.TotalSeconds);
                break;
        }
        RefreshCommands();
    });

    private void TelemetryReceived(Telemetry status) => Dispatch(() =>
    {
        if (Connected)
            UpdateTelemetry(status);
    });

    private void FirmwareVerified(ulong address) => Dispatch(() =>
        SetFirmwareMessage("Ota.Success", "Restart complete: the running firmware image was verified."));

    private void HistoryWritten(ulong address) => Dispatch(() => History.MeasurementWritten(address));

    private void HistoryWriteFailed(Exception error) => Dispatch(() => History.ReportWriteError(error));

    private void PreferenceFailed(Exception error) => Dispatch(() =>
    {
        _log(error);
        Error = Connected
            ? Localizer.Format("Error.RememberController", "Connected, but could not remember the controller: {0}", error.Message)
            : Localizer.Format("Error.ConnectionPreference", "Could not read connection preference: {0}", error.Message);
    });

    /// <summary>Logs and displays a platform or presentation failure.</summary>
    public void ReportError(Exception error)
    {
        _log(error);
        Error = ErrorMessages.Details(error);
    }

    private void AcceptRemote((RemoteConfiguration Config, Telemetry Status) state, bool replaceDraft)
    {
        _remote = state.Config;
        UpdateTelemetry(state.Status);
        if (replaceDraft)
            ReplaceDraft(state.Config.Configuration);
        else
            DraftChanged();
        RefreshCommands();
    }

    private async Task DisconnectAsync()
    {
        await _connection.DisconnectAsync();
        ShowConnection(true);
        Storage = Localizer.Get("Connection.DisconnectedStorage", "Disconnected; remote state not verified.");
        SetMessage("Error.ReconnectPaused", "Automatic reconnection paused. Choose a controller to resume.");
    }

    /// <summary>Stops service work, commands and history reads before releasing their resources.</summary>
    public ValueTask DisposeAsync() => new(_shutdown ??= ShutdownAsync());

    private async Task ShutdownAsync()
    {
        _disposed = true;
        _applicationLifetime.Cancel();
        _scanCts?.Cancel();
        RefreshCommands();
        await _connection.DisposeAsync();
        await Task.WhenAll(Commands.Select(command => command.ExecutionTask).Append(_initialization)
            .Append(_firmwareUpdate is null ? Task.CompletedTask : _firmwareUpdate)
            .Append(_tachCapture ?? Task.CompletedTask));
        await History.DisposeAsync();

        _connection.Changed -= ConnectionChanged;
        _connection.FirmwareVerified -= FirmwareVerified;
        _connection.TelemetryReceived -= TelemetryReceived;
        _connection.HistoryWritten -= HistoryWritten;
        _connection.HistoryWriteFailed -= HistoryWriteFailed;
        _connection.PreferenceFailed -= PreferenceFailed;
        _applicationLifetime.Dispose();
    }

    private void ReplaceDraft(Configuration config)
    {
        Profiles.Clear();
        Fans.Clear();
        for (var i = 0; i < 2; i++)
        {
            var editor = new ProfileEditor(i + 1, config.Profiles[i], ReportError);
            editor.Edited += DraftChanged;
            Profiles.Add(editor);
            UpdateProfileNames(editor);
        }
        for (var i = 0; i < 4; i++)
        {
            var editor = new FanEditor(i + 1, config.Fans[i]);
            editor.PropertyChanged += (_, _) => DraftChanged();
            Fans.Add(editor);
        }
        DraftChanged();
    }

    private DeviceNames ReadDeviceNames()
    {
        try
        {
            return _deviceNamePreferences.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException)
        {
            _log(ex);
            DeviceNamesStatus = Localizer.Format("DeviceNames.LoadError", "Unable to load local names: {0}", ex.Message);
            return DeviceNames.Empty;
        }
    }

    private void DeviceNameChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(TelemetryDisplay.CustomName))
            return;

        SaveDeviceNames();
        UpdateDeviceNameViews();
    }

    private void SaveDeviceNames()
    {
        try
        {
            _deviceNamePreferences.Save(new(
                Sensors.Select(sensor => sensor.CustomName).ToArray(),
                FanReadings.Select(fan => fan.CustomName).ToArray()));
            DeviceNamesStatus = Localizer.Get("DeviceNames.Saved", "Names are saved on this PC only.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            _log(ex);
            DeviceNamesStatus = Localizer.Format("DeviceNames.SaveError", "Local names were not saved: {0}", ex.Message);
        }
    }

    private void UpdateDeviceNameViews()
    {
        History.SetDeviceNames(
            Sensors.Select(sensor => sensor.CustomName).ToArray(),
            FanReadings.Select(fan => fan.CustomName).ToArray());
        foreach (var profile in Profiles)
            UpdateProfileNames(profile);
    }

    private void UpdateProfileNames(ProfileEditor profile)
    {
        var sensorIndex = profile.Sensor - 1;
        var firstFanIndex = sensorIndex * 2;
        profile.SetDeviceNames(
            Sensors[sensorIndex].CustomName,
            FanReadings[firstFanIndex].CustomName,
            FanReadings[firstFanIndex + 1].CustomName);
    }

    private Configuration ReadDraft() => new(1, Profiles.Select(p => p.Read()).ToArray(), Fans.Select(f => f.Read()).ToArray());

    private static string Fingerprint(Configuration config) =>
        JsonSerializer.Serialize(config with
        {
            Fans = config.Fans.Select(f => f.Mode == "auto" ? f with { Duty = 0 } : f).ToArray()
        }, WireJson.Options);

    private void DraftChanged()
    {
        ClearConfigurationMessage();
        try
        {
            var draft = ReadDraft();
            Validation.Config(draft);
            ValidationMessage = "";
            _localDirty = _remote is null || Fingerprint(draft) != Fingerprint(_remote.Configuration);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            ValidationMessage = ErrorMessages.Message(ex);
            _localDirty = true;
        }
        Changed(nameof(LocalState));
        Changed(nameof(SettingsSummary));
        Changed(nameof(SettingsStatus));
        RefreshCommands();
    }

    private void UpdateTelemetry(Telemetry status)
    {
        _bleRssiDbm = status.BleRssiDbm;
        _rssiSupported = status.BleRssiDbm.HasValue ||
            (Version.TryParse(status.FirmwareVersion, out var versionNumber) && versionNumber >= new Version(1, 1, 3));
        Changed(nameof(LinkQuality));
        _remoteDirty = status.Dirty;
        _remoteStorage = status.Storage;
        Changed(nameof(SettingsSummary));
        Changed(nameof(SettingsStatus));
        _lastTelemetry = status;
        Changed(nameof(TachSupported));
        Changed(nameof(CanCaptureTach));
        _firmwareVersion = string.IsNullOrWhiteSpace(status.FirmwareVersion) ? null : status.FirmwareVersion;
        Changed(nameof(Firmware));
        var remoteRam = status.Dirty ? Localizer.Get("Telemetry.NotSaved", "NOT saved") : Localizer.Get("Telemetry.SavedUnchanged", "saved / unchanged");
        var available = Localizer.Get(status.StorageAvailable ? "Telemetry.Yes" : "Telemetry.No", status.StorageAvailable ? "yes" : "no");
        Storage = Localizer.Format("Telemetry.RemoteStorage", "Remote RAM: {0} • NVS: {1} • available: {2}", remoteRam, TranslateStorage(status.Storage), available);
        LastUpdate = Localizer.Format("Telemetry.LastReading", "Last reading: {0:HH:mm:ss} • uptime: {1:g}", DateTime.Now, TimeSpan.FromMilliseconds(status.UptimeMs));
        Changed(nameof(LastUpdate));
        foreach (var sensor in status.Sensors.OrderBy(s => s.Sensor))
        {
            var fault = TranslateFault(sensor.Fault);
            var safety = Localizer.Get(sensor.Safety ? "Telemetry.Active" : "Telemetry.Inactive", sensor.Safety ? "active" : "inactive");
            Sensors[sensor.Sensor - 1].Update(sensor.Temperature is { } t ? $"{t:F1} °C" : Localizer.Get("State.Unavailable", "Unavailable"),
                Localizer.Format("Telemetry.SensorDetails", "{0:F1} mV • {1} • safety: {2}", sensor.Millivolts, fault, safety),
                Localizer.Format("Telemetry.SensorSummary", "{0} • safety: {1}", fault, safety));
        }
        foreach (var fan in status.Fans.OrderBy(f => f.Fan))
        {
            var mode = Localizer.Get(fan.Mode == "auto" ? "Telemetry.Automatic" : "Telemetry.Fixed", fan.Mode == "auto" ? "Automatic" : "Fixed");
            var boost = Localizer.Get(fan.Boost ? "Telemetry.Active" : "Telemetry.Inactive", fan.Boost ? "active" : "inactive");
            var pwmFault = fan.PwmOk ? "" : Localizer.Get("Telemetry.PwmFault", " • PWM OUTPUT FAULT");
            var pwmStatus = fan.PwmOk ? Localizer.Get("Telemetry.PwmOk", "OK") : Localizer.Get("Telemetry.PwmFaultState", "FAULT");
            var rpmText = Localizer.Format("Telemetry.Rpm", "{0:N0} RPM", fan.Rpm);
            FanReadings[fan.Fan - 1].Update(rpmText,
                Localizer.Format("Telemetry.FanDetails", "{0} • fixed {1:F1} % • target {2:F1} % • PWM {3:F1} %\nPWM output: {4} • boost: {5}",
                    mode, fan.FixedDuty, fan.TargetDuty, fan.AppliedDuty, pwmStatus, boost),
                Localizer.Format("Telemetry.FanSummary", "{0} • PWM {1:F1} %{2}", mode, fan.AppliedDuty, pwmFault));
        }
    }

    private static string TranslateFault(string value) => value switch
    {
        "none" => Localizer.Get("Telemetry.Fault.None", "no fault"),
        "short_circuit" => Localizer.Get("Telemetry.Fault.ShortCircuit", "SHORT CIRCUIT"),
        "open_circuit" => Localizer.Get("Telemetry.Fault.OpenCircuit", "OPEN CIRCUIT"),
        "out_of_range" => Localizer.Get("Telemetry.Fault.OutOfRange", "OUT OF RANGE"),
        _ => Localizer.Format("Telemetry.Fault.Unknown", "unknown fault ({0})", value)
    };
    private static string TranslateStorage(string value) => value switch
    {
        "defaults" => Localizer.Get("Telemetry.Storage.defaults", "factory defaults"),
        "loaded" => Localizer.Get("Telemetry.Storage.loaded", "loaded"),
        "saved" => Localizer.Get("Telemetry.Storage.saved", "saved"),
        "invalid" => Localizer.Get("Telemetry.Storage.invalid", "INVALID"),
        "unavailable" => Localizer.Get("Telemetry.Storage.unavailable", "UNAVAILABLE"),
        "write_error" => Localizer.Get("Telemetry.Storage.write_error", "WRITE ERROR"),
        "verify_error" => Localizer.Get("Telemetry.Storage.verify_error", "VERIFICATION ERROR"),
        _ => value
    };
}
