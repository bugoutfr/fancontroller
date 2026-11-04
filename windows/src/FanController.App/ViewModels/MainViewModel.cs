using System.Collections.ObjectModel;
using System.Text.Json;
using FanController.App.Bluetooth;
using FanController.App.Localization;
using FanController.Core;
using Microsoft.UI.Dispatching;

namespace FanController.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly CancellationTokenSource _applicationLifetime = new();
    private readonly ReconnectPolicy _reconnect = new();
    private readonly ConnectionPreferences _preferences = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanController", "last-device.txt"));
    private ulong? _lastDevice;
    private bool _initialized, _disposed;
    private bool _launchAtStartup;
    public bool LaunchAtStartup
    {
        get => _launchAtStartup;
        set
        {
            if (_launchAtStartup == value) return;
            try
            {
                WindowsStartup.SetEnabled(value);
                Set(ref _launchAtStartup, value);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Diagnostics.Record(ex);
                Error = Localizer.Format("Error.WindowsStartup", "Windows startup: {0}", ex.Message);
                Changed(nameof(LaunchAtStartup));
            }
        }
    }
    private bool _showConnection = true;
    private bool _connecting;
    public Microsoft.UI.Xaml.Visibility ConnectionVisibility => _showConnection
        ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility ConnectedDetailsVisibility => Connected
        ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility DisconnectVisibility => DisconnectCommand.CanExecute(null)
        ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string DisconnectLabel => Localizer.Get(Connected ? "Action.Disconnect" : "Action.PauseReconnect", Connected ? "Disconnect" : "Pause reconnection");
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
        Changed(nameof(ConnectionVisibility));
    }
    private BleScanner? _scanner;
    private CancellationTokenSource? _scanCts, _connectionCts;
    private ProtocolSession? _session;
    private ControllerService? _controller;
    private RemoteConfiguration? _remote;
    private bool _busy, _scanning, _connected, _localDirty;
    private bool _remoteDirty;
    private string _remoteStorage = "";
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
    public string ControllerName { get => _controllerName; private set => Set(ref _controllerName, value); }
    public string ControllerIdentifier => Localizer.Format("Connection.Address", "Bluetooth address: {0}",
        _connectedAddress == 0 ? "—" : string.Join(":", _connectedAddress.ToString("X12").Chunk(2).Select(chars => new string(chars))));
    private int? _bleRssiDbm;
    private bool _rssiSupported;
    public string LinkQuality => !Connected ? Localizer.Get("Connection.LinkInactive", "Link inactive • quality unavailable") :
        _bleRssiDbm is { } rssi
            ? Localizer.Format("Connection.Rssi", "Signal received by ESP32: {0} dBm", rssi)
            : _rssiSupported ? Localizer.Get("Connection.RssiTemporaryUnavailable", "Radio signal temporarily unavailable") :
                Localizer.Get("Connection.RssiFirmwareUnavailable", "Radio signal unavailable • firmware 1.1.3 or later required");
    private string _configurationMessageKey = "", _configurationMessageFallback = "";
    public string ConfigurationMessage => string.IsNullOrEmpty(_configurationMessageKey) ? "" :
        Localizer.Get(_configurationMessageKey, _configurationMessageFallback);
    private string _historySession = Guid.NewGuid().ToString("N");
    private bool _curveEditing;
    public bool CurveEditing
    {
        get => _curveEditing;
        set
        {
            if (!Set(ref _curveEditing, value)) return;
            Changed(nameof(CurveEditorVisibility));
            RefreshCommands();
        }
    }
    public Microsoft.UI.Xaml.Visibility CurveEditorVisibility => CurveEditing
        ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    private string? _firmwareVersion;
    public string Firmware => _firmwareVersion is null
        ? Localizer.Get("Firmware.Disconnected", "Firmware: disconnected")
        : Localizer.Format("Firmware.Version", "Firmware {0}", _firmwareVersion);
    public HistoryViewModel History { get; }
    public ObservableCollection<BleDeviceDisplay> Devices { get; } = [];
    public ObservableCollection<ProfileEditor> Profiles { get; } = [];
    public ObservableCollection<FanEditor> Fans { get; } = [];
    public ObservableCollection<SensorDisplay> Sensors { get; } = [];
    public ObservableCollection<FanDisplay> FanReadings { get; } = [];
    public BleDeviceDisplay? SelectedDevice
    {
        get => _selectedDevice;
        set { Set(ref _selectedDevice, value); RefreshCommands(); }
    }
    public bool Busy { get => _busy; private set { Set(ref _busy, value); Changed(nameof(CanEdit)); RefreshCommands(); } }
    public bool Connected { get => _connected; private set { Set(ref _connected, value); Changed(nameof(ConnectionLabel)); Changed(nameof(LinkQuality)); Changed(nameof(CanEdit)); Changed(nameof(SettingsSummary)); RefreshCommands(); } }
    public bool CanEdit => Connected && !Busy;
    public string ConnectionLabel => Localizer.Get(Connected ? "Connection.Connected" : "Connection.Disconnected",
        Connected ? "Connected • local BLE" : "Disconnected • readings are stale");
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
    }
    private void ClearConfigurationMessage()
    {
        _configurationMessageKey = "";
        _configurationMessageFallback = "";
        Changed(nameof(ConfigurationMessage));
    }
    public string Error { get => _error; private set { Set(ref _error, value); Changed(nameof(HasError)); Changed(nameof(ErrorVisibility)); } }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public Microsoft.UI.Xaml.Visibility ErrorVisibility => HasError
        ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string ValidationMessage { get => _validation; private set => Set(ref _validation, value); }
    public string Storage { get => _storage; private set => Set(ref _storage, value); }
    public string LocalState => Profiles.Count == 0 ? Localizer.Get("Settings.NoConfiguration", "No configuration has been read.") :
        _localDirty ? Localizer.Get("Settings.LocalChanges", "Local changes NOT applied") :
        Localizer.Get("Settings.DraftMatches", "Draft matches the last configuration read");
    private string _lastUpdate = "No readings received";
    public string LastUpdate { get => _lastUpdate; private set => Set(ref _lastUpdate, value); }
    private Telemetry? _lastTelemetry;
    public AsyncCommand ScanCommand { get; }
    public AsyncCommand ConnectCommand { get; }
    public AsyncCommand DisconnectCommand { get; }
    public AsyncCommand ReadCommand { get; }
    public AsyncCommand AdoptCommand { get; }
    public AsyncCommand ApplyCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand DefaultsCommand { get; }
    private AsyncCommand[] Commands => [ScanCommand, ConnectCommand, DisconnectCommand, ReadCommand,
        AdoptCommand, ApplyCommand, SaveCommand, DefaultsCommand];

    public void RefreshLocalization()
    {
        Changed(nameof(DisconnectLabel));
        Changed(nameof(ConnectionHeading));
        Changed(nameof(SettingsSummary));
        Changed(nameof(ControllerIdentifier));
        Changed(nameof(LinkQuality));
        Changed(nameof(ConfigurationMessage));
        Changed(nameof(Firmware));
        Changed(nameof(ConnectionLabel));
        Changed(nameof(Message));
        Changed(nameof(LocalState));
        if (_lastTelemetry is not null) UpdateTelemetry(_lastTelemetry);
        else
        {
            var waiting = Localizer.Get("State.Waiting", "Waiting");
            var noData = Localizer.Get("State.NoData", "No readings received.");
            foreach (var sensor in Sensors) sensor.SetWaiting(waiting, noData);
            foreach (var fan in FanReadings) fan.SetWaiting(waiting, noData);
        }
        foreach (var profile in Profiles) profile.RefreshLocalization();
        foreach (var fan in Fans) fan.RefreshLocalization();
        History.RefreshLocalization();
    }

    public MainViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        History = new(dispatcher);
        for (var sensor = 1; sensor <= 2; sensor++)
            Sensors.Add(new(Localizer.Format("Sensor.Title", "Probe {0}", sensor)));
        for (var fan = 1; fan <= 4; fan++)
            FanReadings.Add(new(Localizer.Format("Fan.Title", "Fan {0} • probe {1}", fan, (fan - 1) / 2 + 1)));
        ScanCommand = Command(ScanAsync, () => !Busy && !Connected && !_scanning);
        ConnectCommand = Command(ConnectAsync, () => !Busy && !Connected && SelectedDevice is not null);
        DisconnectCommand = Command(() => { Disconnect(); return Task.CompletedTask; },
            () => !_reconnect.Paused && (_session is not null || _lastDevice is not null));
        ReadCommand = Command(() => Operation(async () =>
        {
            await AcceptRemoteAsync(await _controller!.SynchronizeAsync(_connectionCts!.Token), replaceDraft: false);
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
            await AcceptRemoteAsync(await _controller!.ApplyAsync(draft, _connectionCts!.Token), replaceDraft: true);
            SetConfigurationMessage("Settings.AppliedRam", "Configuration applied to RAM. Save to the ESP32 to keep it after restart.");
        }), () => Ready() && _localDirty && string.IsNullOrEmpty(ValidationMessage));
        SaveCommand = Command(() => Operation(async () =>
        {
            await AcceptRemoteAsync(await _controller!.SaveAsync(_connectionCts!.Token), replaceDraft: false);
            SetConfigurationMessage("Settings.Saved", "Configuration saved to and reread from the ESP32.");
        }), () => Ready() && !_localDirty);
        DefaultsCommand = Command(() => Operation(async () =>
        {
            await AcceptRemoteAsync(await _controller!.DefaultsAsync(_connectionCts!.Token), replaceDraft: true);
            SetConfigurationMessage("Settings.DefaultsRam", "Factory defaults applied to RAM. Saving to the ESP32 is a separate step.");
        }), Ready);
    }

    private AsyncCommand Command(Func<Task> action, Func<bool> enabled) => new(action, enabled, ex =>
    {
        Diagnostics.Record(ex);
        Error = ErrorDetails(ex);
    });
    private static string ErrorDetails(Exception exception)
    {
        var details = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            details.Add(string.IsNullOrWhiteSpace(message)
                ? current.GetType().Name
                : $"{current.GetType().Name}: {message}");
        }
        return string.Join(Environment.NewLine, details.Distinct(StringComparer.Ordinal));
    }
    private bool Ready() => Connected && !Busy;
    private void RefreshCommands()
    {
        foreach (var command in Commands) command?.Refresh();
        foreach (var profile in Profiles) profile.IsEditing = CurveEditing && CanEdit;
        Changed(nameof(ConnectedDetailsVisibility));
        Changed(nameof(DisconnectVisibility));
        Changed(nameof(DisconnectLabel));
        Changed(nameof(ConnectionHeading));
    }
    private void Dispatch(Action action)
    {
        if (_dispatcher.HasThreadAccess) action(); else _dispatcher.TryEnqueue(() => action());
    }
    private async Task Operation(Func<Task> action)
    {
        Busy = true;
        try { await action(); }
        finally { Busy = false; }
    }
    private async Task ScanAsync()
    {
        _reconnect.Pause();
        _scanning = true;
        RefreshCommands();
        Devices.Clear();
        SelectedDevice = null;
        SetMessage("Connection.Searching", "Searching for controllers…");
        var scanner = new BleScanner();
        _scanner = scanner;
        _scanCts = new();
        scanner.Found += candidate => Dispatch(() =>
        {
            if (_scanner != scanner) return;
            var old = Devices.FirstOrDefault(d => d.Address == candidate.Address);
            if (old is null) Devices.Add(new(candidate));
            else old.Update(candidate);
        });
        scanner.Failed += text => Dispatch(() => Error = text);
        try
        {
            scanner.Start();
            await Task.Delay(TimeSpan.FromSeconds(15), _scanCts.Token);
            SetMessage(Devices.Count == 0 ? "Error.NoController" : "Connection.SearchComplete",
                Devices.Count == 0 ? "No controller found. Check power, Bluetooth, and range." : "Search complete.");
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_scanner == scanner) { scanner.Dispose(); _scanner = null; }
            _scanning = false;
            RefreshCommands();
        }
    }
    private async Task ConnectAsync()
    {
        _reconnect.Resume();
        await ConnectAddressAsync(SelectedDevice!.Address, automatic: false);
    }

    private async Task ConnectAddressAsync(ulong address, bool automatic)
    {
        await Operation(async () =>
        {
            _scanCts?.Cancel();
            _scanner?.Dispose();
            _scanner = null;
            CloseSession();
            _connectedAddress = address;
            ControllerName = Localizer.Get("Connection.ReadingName", "Reading controller name…");
            Changed(nameof(ControllerIdentifier));
            _historySession = Guid.NewGuid().ToString("N");
            History.SelectDevice(address);
            var transport = new WindowsBleTransport();
            var session = new ProtocolSession(transport);
            _session = session;
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_applicationLifetime.Token);
            lifetime.CancelAfter(TimeSpan.FromSeconds(35));
            _connectionCts = lifetime;
            session.Invalidated += ex => Dispatch(() =>
            {
                if (_session != session) return;
                lifetime.Cancel();
                Connected = false;
                ShowConnection(true);
                Storage = Localizer.Get("Connection.RemoteStateUncertain", "Remote state is uncertain; reconnect and reread it.");
                Error = ErrorDetails(ex);
                SetMessage("Connection.LinkInterrupted", "Link interrupted.");
                SetConfigurationMessage("Settings.InterruptedDraft", "Draft retained. An interrupted operation may have been partly applied; check the configuration after reconnecting.");
            });
            SetConnecting(true);
            try
            {
                SetMessage(automatic ? "Connection.AutoConnecting" : "Connection.Connecting",
                    automatic ? "Reconnecting to the last controller…" : "Connecting to controller…");
                await transport.ConnectAsync(address, lifetime.Token, allowPairing: !automatic);
                ControllerName = string.IsNullOrWhiteSpace(transport.DeviceName) ? Localizer.Get("Connection.NameUnavailable", "Controller name unavailable") : transport.DeviceName;
                // Le délai de découverte ne doit pas raccourcir les 20 s de chaque requête.
                lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
                _controller = new(session);
                SetMessage("Connection.ReadingSettings", "Reading settings…");
                var state = await _controller.SynchronizeAsync(lifetime.Token);
                await AcceptRemoteAsync(state, replaceDraft: Profiles.Count == 0);
                Error = "";
                Connected = true;
                ShowConnection(false);
                _lastDevice = address;
                _reconnect.Succeeded();
                try { _preferences.Save(address); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Diagnostics.Record(ex);
                    Error = Localizer.Format("Error.RememberController", "Connected, but could not remember the controller: {0}", ex.Message);
                }
                SetMessage("Connection.Established", "Encrypted Bluetooth link established.");
                _ = PollAsync(_controller, session, lifetime.Token);
            }
            catch
            {
                SetMessage(_reconnect.Paused ? "Error.ReconnectPaused" : "Error.ControllerUnavailable",
                    _reconnect.Paused ? "Automatic reconnection paused. Choose a controller to resume." : "Controller unavailable. Check its power and range.");
                CloseSession();
                ShowConnection(true);
                throw;
            }
            finally { SetConnecting(false); }
        });
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        try { Set(ref _launchAtStartup, WindowsStartup.Enabled(), nameof(LaunchAtStartup)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Diagnostics.Record(ex);
            Error = Localizer.Format("Error.WindowsStartup", "Could not read Windows startup setting: {0}", ex.Message);
        }
        try { _lastDevice = _preferences.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Record(ex);
            Error = Localizer.Format("Error.ConnectionPreference", "Could not read connection preference: {0}", ex.Message);
        }
        _ = ReconnectLoopAsync();
        await History.InitializeAsync();
    }

    private async Task ReconnectLoopAsync()
    {
        var loop = new AutoReconnectLoop(_reconnect,
            () => !_disposed && !Connected && !Busy && !_scanning ? _lastDevice : null,
            TryAutoConnectAsync,
            ex =>
            {
                Diagnostics.Record(ex);
                if (_disposed) return;
                Error = ErrorDetails(ex);
                ShowConnection(true);
                if (_reconnect.Paused) SetMessage("Error.ReconnectPaused", "Automatic reconnection paused. Choose a controller to resume.");
                else SetMessage("Error.Retrying", "Automatic reconnection • retrying in {0:0} s.", _reconnect.Delay.TotalSeconds);
            });
        await loop.RunAsync(_applicationLifetime.Token);
    }

    private async Task<bool> TryAutoConnectAsync(ulong address, CancellationToken ct)
    {
        Busy = true;
        SetConnecting(true);
        SetMessage("Connection.AutoConnecting", "Reconnecting to the last controller…");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            var paired = await WindowsBleTransport.IsPairedAsync(address, timeout.Token);
            if (_disposed || _reconnect.Paused) return false;
            if (!paired)
            {
                ShowConnection(true);
                SetMessage("Connection.ManualRequired", "The last controller is no longer paired in Windows. Manual connection required.");
                return false;
            }
            await ConnectAddressAsync(address, automatic: true);
            return Connected;
        }
        finally { SetConnecting(false); Busy = false; }
    }
    private async Task PollAsync(ControllerService controller, ProtocolSession session, CancellationToken ct)
    {
        var address = _connectedAddress;
        var historySession = _historySession;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                var status = await controller.StatusAsync(ct).ConfigureAwait(false);
                Dispatch(() =>
                {
                    if (_session != session || !Connected) return;
                    UpdateTelemetry(status);
                });
                await History.RecordAsync(status, address, historySession, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Diagnostics.Record(ex);
            Dispatch(() => { if (_session == session) Error = ErrorDetails(ex); });
        }
    }
    private async Task AcceptRemoteAsync((RemoteConfiguration Config, Telemetry Status) state, bool replaceDraft)
    {
        _remote = state.Config;
        UpdateTelemetry(state.Status);
        if (replaceDraft) ReplaceDraft(state.Config.Configuration);
        else DraftChanged();
        RefreshCommands();
        await History.RecordAsync(state.Status, _connectedAddress, _historySession, _connectionCts!.Token);
    }
    private void ReplaceDraft(Configuration config)
    {
        Profiles.Clear();
        Fans.Clear();
        for (var i = 0; i < 2; i++)
        {
            var editor = new ProfileEditor(i + 1, config.Profiles[i]);
            editor.Edited += DraftChanged;
            Profiles.Add(editor);
        }
        for (var i = 0; i < 4; i++)
        {
            var editor = new FanEditor(i + 1, config.Fans[i]);
            editor.PropertyChanged += (_, _) => DraftChanged();
            Fans.Add(editor);
        }
        DraftChanged();
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
        catch (ArgumentException ex) { ValidationMessage = ex.Message; _localDirty = true; }
        catch (InvalidDataException ex) { ValidationMessage = ex.Message; _localDirty = true; }
        Changed(nameof(LocalState));
        Changed(nameof(SettingsSummary));
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
        _lastTelemetry = status;
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
            FanReadings[fan.Fan - 1].Update($"{fan.Rpm:N0} RPM",
                Localizer.Format("Telemetry.FanDetails", "{0} • fixed {1:F1} % • target {2:F1} % • PWM {3:F1} %\nPWM output: {4} • boost: {5}",
                    mode, fan.FixedDuty, fan.TargetDuty, fan.AppliedDuty, fan.PwmOk ? "OK" : "FAULT", boost),
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
    private void CloseSession()
    {
        var old = _session;
        _session = null;
        _connectionCts?.Cancel();
        // Ne pas disposer le CTS tant que le polling peut encore observer son jeton.
        old?.Dispose();
        _controller = null;
        _bleRssiDbm = null;
        _rssiSupported = false;
        Connected = false;
        RefreshCommands();
    }
    private void Disconnect()
    {
        _reconnect.Pause();
        CloseSession();
        ShowConnection(true);
        Storage = Localizer.Get("Connection.DisconnectedStorage", "Disconnected; remote state not verified.");
        SetMessage("Error.ReconnectPaused", "Automatic reconnection paused. Choose a controller to resume.");
    }
    public void Dispose()
    {
        _disposed = true;
        _applicationLifetime.Cancel();
        _scanCts?.Cancel();
        _scanner?.Dispose();
        _scanner = null;
        CloseSession();
    }
}
