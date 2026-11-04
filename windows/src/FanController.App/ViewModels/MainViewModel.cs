using System.Collections.ObjectModel;
using System.Text.Json;
using FanController.App.Bluetooth;
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
                Error = $"Windows startup: {ex.Message}";
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
    public string DisconnectLabel => Connected ? "Disconnect" : "Pause reconnection";
    public string ConnectionHeading => Connected ? "Connected" : _connecting ? "Connecting…" : "Disconnected";
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
    public string SettingsSummary => !Connected ? "Configuration not refreshed: connect the controller." :
        _remoteStorage is "invalid" or "unavailable" or "write_error" or "verify_error"
            ? $"ESP32 save problem: {TranslateStorage(_remoteStorage)}." :
        _localDirty ? "Changes need to be applied." :
        _remoteDirty ? "Settings applied. Save them to keep them after restart." :
        "Configuration loaded from the ESP32.";
    private string _message = "Search for a controller to connect.";
    private string _error = "", _validation = "", _storage = "Remote state unknown.";
    private BleDeviceDisplay? _selectedDevice;
    private ulong _connectedAddress;
    private string _controllerName = "No controller connected";
    public string ControllerName { get => _controllerName; private set => Set(ref _controllerName, value); }
    public string ControllerIdentifier => _connectedAddress == 0 ? "Bluetooth address: —" :
        $"Bluetooth address: {string.Join(":", _connectedAddress.ToString("X12").Chunk(2).Select(chars => new string(chars)))}";
    private int? _bleRssiDbm;
    private bool _rssiSupported;
    public string LinkQuality => !Connected ? "Link inactive • quality unavailable" :
        _bleRssiDbm is { } rssi
            ? $"Signal received by ESP32: {rssi} dBm"
            : _rssiSupported ? "Radio signal temporarily unavailable" :
                "Radio signal unavailable • firmware 1.1.3 or later required";
    private string _configurationMessage = "";
    public string ConfigurationMessage { get => _configurationMessage; private set => Set(ref _configurationMessage, value); }
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
    private string _firmware = "Firmware: disconnected";
    public string Firmware { get => _firmware; private set => Set(ref _firmware, value); }
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
    public string ConnectionLabel => Connected ? "Connected • local BLE" : "Disconnected • readings are stale";
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string Error { get => _error; private set { Set(ref _error, value); Changed(nameof(HasError)); Changed(nameof(ErrorVisibility)); } }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public Microsoft.UI.Xaml.Visibility ErrorVisibility => HasError
        ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string ValidationMessage { get => _validation; private set => Set(ref _validation, value); }
    public string Storage { get => _storage; private set => Set(ref _storage, value); }
    public string LocalState => Profiles.Count == 0 ? "No configuration has been read." :
        _localDirty ? "Local changes NOT applied" : "Draft matches the last configuration read";
    public string LastUpdate { get; private set; } = "No readings received";
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

    public MainViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        History = new(dispatcher);
        for (var sensor = 1; sensor <= 2; sensor++)
            Sensors.Add(new($"Temperature {sensor}"));
        for (var fan = 1; fan <= 4; fan++)
            FanReadings.Add(new($"Fan {fan} • Temperature {(fan - 1) / 2 + 1}"));
        ScanCommand = Command(ScanAsync, () => !Busy && !Connected && !_scanning);
        ConnectCommand = Command(ConnectAsync, () => !Busy && !Connected && SelectedDevice is not null);
        DisconnectCommand = Command(() => { Disconnect(); return Task.CompletedTask; },
            () => !_reconnect.Paused && (_session is not null || _lastDevice is not null));
        ReadCommand = Command(() => Operation(async () =>
        {
            await AcceptRemoteAsync(await _controller!.SynchronizeAsync(_connectionCts!.Token), replaceDraft: false);
            ConfigurationMessage = "Configuration reread; draft retained. No changes were reapplied.";
        }), Ready);
        AdoptCommand = Command(() =>
        {
            ReplaceDraft(_remote!.Configuration);
            ConfigurationMessage = "Changes discarded without writing to the ESP32.";
            return Task.CompletedTask;
        }, () => !Busy && _remote is not null && _localDirty);
        ApplyCommand = Command(() => Operation(async () =>
        {
            var draft = ReadDraft();
            await AcceptRemoteAsync(await _controller!.ApplyAsync(draft, _connectionCts!.Token), replaceDraft: true);
            ConfigurationMessage = "Configuration applied to RAM. Save to the ESP32 to keep it after restart.";
        }), () => Ready() && _localDirty && string.IsNullOrEmpty(ValidationMessage));
        SaveCommand = Command(() => Operation(async () =>
        {
            await AcceptRemoteAsync(await _controller!.SaveAsync(_connectionCts!.Token), replaceDraft: false);
            ConfigurationMessage = "Configuration saved to and reread from the ESP32.";
        }), () => Ready() && !_localDirty);
        DefaultsCommand = Command(() => Operation(async () =>
        {
            await AcceptRemoteAsync(await _controller!.DefaultsAsync(_connectionCts!.Token), replaceDraft: true);
            ConfigurationMessage = "Factory defaults applied to RAM. Saving to the ESP32 is a separate step.";
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
        Message = "Searching for controllers…";
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
            Message = Devices.Count == 0 ? "No controller found. Check power, Bluetooth, and range." : "Search complete.";
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
            ControllerName = "Reading controller name…";
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
                Storage = "Remote state is uncertain; reconnect and reread it.";
                Error = ErrorDetails(ex);
                Message = "Link interrupted.";
                ConfigurationMessage = "Draft retained. An interrupted operation may have been partly applied; check the configuration after reconnecting.";
            });
            SetConnecting(true);
            try
            {
                Message = automatic ? "Reconnecting to the last controller…" : "Connecting to controller…";
                await transport.ConnectAsync(address, lifetime.Token, allowPairing: !automatic);
                ControllerName = string.IsNullOrWhiteSpace(transport.DeviceName) ? "Controller name unavailable" : transport.DeviceName;
                // Le délai de découverte ne doit pas raccourcir les 20 s de chaque requête.
                lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
                _controller = new(session);
                Message = "Reading settings…";
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
                    Error = $"Connected, but could not remember the controller: {ex.Message}";
                }
                Message = "Encrypted Bluetooth link established.";
                _ = PollAsync(_controller, session, lifetime.Token);
            }
            catch
            {
                Message = _reconnect.Paused
                    ? "Automatic reconnection paused. Choose a controller to resume."
                    : "Controller unavailable. Check its power and range.";
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
            Error = $"Could not read Windows startup setting: {ex.Message}";
        }
        try { _lastDevice = _preferences.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Record(ex);
            Error = $"Could not read connection preference: {ex.Message}";
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
                Message = _reconnect.Paused
                    ? "Automatic reconnection paused. Choose a controller to resume."
                    : $"Automatic reconnection • retrying in {_reconnect.Delay.TotalSeconds:0} s.";
            });
        await loop.RunAsync(_applicationLifetime.Token);
    }

    private async Task<bool> TryAutoConnectAsync(ulong address, CancellationToken ct)
    {
        Busy = true;
        SetConnecting(true);
        Message = "Reconnecting to the last controller…";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            var paired = await WindowsBleTransport.IsPairedAsync(address, timeout.Token);
            if (_disposed || _reconnect.Paused) return false;
            if (!paired)
            {
                ShowConnection(true);
                Message = "The last controller is no longer paired in Windows. Manual connection required.";
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
        ConfigurationMessage = "";
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
        Firmware = status.FirmwareVersion is { Length: > 0 } version
            ? $"Firmware {version}" : "Legacy firmware • version not published";
        Storage = $"Remote RAM: {(status.Dirty ? "NOT saved" : "saved / unchanged")} • " +
                  $"NVS: {TranslateStorage(status.Storage)} • available: {(status.StorageAvailable ? "yes" : "no")}";
        LastUpdate = $"Last reading: {DateTime.Now:HH:mm:ss} • uptime: {TimeSpan.FromMilliseconds(status.UptimeMs):g}";
        Changed(nameof(LastUpdate));
        foreach (var sensor in status.Sensors.OrderBy(s => s.Sensor))
            Sensors[sensor.Sensor - 1].Update(sensor.Temperature is { } t ? $"{t:F1} °C" : "Unavailable",
                $"{sensor.Millivolts:F1} mV • {TranslateFault(sensor.Fault)} • safety: {(sensor.Safety ? "ACTIVE" : "inactive")}",
                $"{TranslateFault(sensor.Fault)} • safety: {(sensor.Safety ? "ACTIVE" : "inactive")}");
        foreach (var fan in status.Fans.OrderBy(f => f.Fan))
            FanReadings[fan.Fan - 1].Update($"{fan.Rpm:N0} RPM",
                $"{(fan.Mode == "auto" ? "Automatic" : "Fixed")} • fixed {fan.FixedDuty:F1} % • target {fan.TargetDuty:F1} % • PWM {fan.AppliedDuty:F1} %\n" +
                $"PWM output: {(fan.PwmOk ? "OK" : "FAULT")} • boost: {(fan.Boost ? "active" : "inactive")}",
                $"{(fan.Mode == "auto" ? "Automatic" : "Fixed")} • PWM {fan.AppliedDuty:F1} %" +
                (fan.PwmOk ? "" : " • PWM OUTPUT FAULT"));
    }

    private static string TranslateFault(string value) => value switch
    {
        "none" => "no fault",
        "short_circuit" => "SHORT CIRCUIT",
        "open_circuit" => "OPEN CIRCUIT",
        "out_of_range" => "OUT OF RANGE",
        _ => $"unknown fault ({value})"
    };
    private static string TranslateStorage(string value) => value switch
    {
        "defaults" => "factory defaults",
        "loaded" => "loaded",
        "saved" => "saved",
        "invalid" => "INVALID",
        "unavailable" => "UNAVAILABLE",
        "write_error" => "WRITE ERROR",
        "verify_error" => "VERIFICATION ERROR",
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
        Storage = "Disconnected; remote state not verified.";
        Message = "Automatic reconnection paused. Choose a controller to resume.";
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
