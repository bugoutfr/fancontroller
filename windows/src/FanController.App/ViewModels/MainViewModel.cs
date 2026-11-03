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
                Error = $"Démarrage Windows : {ex.Message}";
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
    public string DisconnectLabel => Connected ? "Déconnecter" : "Suspendre la reconnexion";
    public string ConnectionHeading => Connected ? "Connecté" : _connecting ? "Connexion en cours…" : "Déconnecté";
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
    public string SettingsSummary => !Connected ? "Configuration non actualisée : connectez le contrôleur." :
        _remoteStorage is "invalid" or "unavailable" or "write_error" or "verify_error"
            ? $"Problème d’enregistrement sur l’ESP32 : {TranslateStorage(_remoteStorage)}." :
        _localDirty ? "Modifications à appliquer." :
        _remoteDirty ? "Réglages appliqués. Enregistrez-les pour les conserver après redémarrage." :
        "Configuration chargée depuis l’ESP32.";
    private string _message = "Recherchez un contrôleur pour vous connecter.";
    private string _error = "", _validation = "", _storage = "État distant inconnu.";
    private BleDeviceDisplay? _selectedDevice;
    private ulong _connectedAddress;
    private string _controllerName = "Aucun contrôleur connecté";
    public string ControllerName { get => _controllerName; private set => Set(ref _controllerName, value); }
    public string ControllerIdentifier => _connectedAddress == 0 ? "Adresse BLE : —" :
        $"Adresse BLE : {string.Join(":", _connectedAddress.ToString("X12").Chunk(2).Select(chars => new string(chars)))}";
    private int? _bleRssiDbm;
    private bool _rssiSupported;
    public string LinkQuality => !Connected ? "Liaison inactive • qualité non disponible" :
        _bleRssiDbm is { } rssi
            ? $"Signal reçu par l’ESP32 : {rssi} dBm"
            : _rssiSupported ? "Signal radio momentanément indisponible" :
                "Signal radio indisponible • firmware 1.1.3 ou ultérieur requis";
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
    private string _firmware = "Firmware : non connecté";
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
    public string ConnectionLabel => Connected ? "Connecté • BLE local" : "Déconnecté • mesures non actuelles";
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string Error { get => _error; private set { Set(ref _error, value); Changed(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public string ValidationMessage { get => _validation; private set => Set(ref _validation, value); }
    public string Storage { get => _storage; private set => Set(ref _storage, value); }
    public string LocalState => Profiles.Count == 0 ? "Aucune configuration lue." :
        _localDirty ? "Modifications locales NON appliquées" : "Brouillon identique à la dernière configuration lue";
    public string LastUpdate { get; private set; } = "Aucune mesure reçue";
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
            Sensors.Add(new($"Sonde {sensor}"));
        for (var fan = 1; fan <= 4; fan++)
            FanReadings.Add(new($"Ventilateur {fan} • sonde {(fan - 1) / 2 + 1}"));
        ScanCommand = Command(ScanAsync, () => !Busy && !Connected && !_scanning);
        ConnectCommand = Command(ConnectAsync, () => !Busy && !Connected && SelectedDevice is not null);
        DisconnectCommand = Command(() => { Disconnect(); return Task.CompletedTask; },
            () => !_reconnect.Paused && (_session is not null || _lastDevice is not null));
        ReadCommand = Command(() => Operation(async () =>
        {
            await AcceptRemoteAsync(await _controller!.SynchronizeAsync(_connectionCts!.Token), replaceDraft: false);
            ConfigurationMessage = "Configuration relue ; brouillon conservé. Aucun changement réappliqué.";
        }), Ready);
        AdoptCommand = Command(() =>
        {
            ReplaceDraft(_remote!.Configuration);
            ConfigurationMessage = "Modifications annulées, sans écriture sur l’ESP32.";
            return Task.CompletedTask;
        }, () => !Busy && _remote is not null && _localDirty);
        ApplyCommand = Command(() => Operation(async () =>
        {
            var draft = ReadDraft();
            await AcceptRemoteAsync(await _controller!.ApplyAsync(draft, _connectionCts!.Token), replaceDraft: true);
            ConfigurationMessage = "Configuration appliquée en RAM. Cliquez sur Enregistrer sur l’ESP32 pour la conserver après redémarrage.";
        }), () => Ready() && _localDirty && string.IsNullOrEmpty(ValidationMessage));
        SaveCommand = Command(() => Operation(async () =>
        {
            await AcceptRemoteAsync(await _controller!.SaveAsync(_connectionCts!.Token), replaceDraft: false);
            ConfigurationMessage = "Configuration enregistrée sur l’ESP32 et relue.";
        }), () => Ready() && !_localDirty);
        DefaultsCommand = Command(() => Operation(async () =>
        {
            await AcceptRemoteAsync(await _controller!.DefaultsAsync(_connectionCts!.Token), replaceDraft: true);
            ConfigurationMessage = "Valeurs d’usine appliquées en RAM. Enregistrement sur l’ESP32 séparé.";
        }), Ready);
    }

    private AsyncCommand Command(Func<Task> action, Func<bool> enabled) => new(action, enabled, ex =>
    {
        Diagnostics.Record(ex);
        Error = ex.Message;
    });
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
        Error = "";
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
        Error = "";
        Message = "Recherche de contrôleurs…";
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
            Message = Devices.Count == 0 ? "Aucun contrôleur trouvé. Vérifiez alimentation, radio et portée." : "Recherche terminée.";
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
            ControllerName = "Nom du contrôleur en cours de lecture…";
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
                Storage = "État distant incertain ; reconnexion et relecture obligatoires.";
                Error = ex.Message;
                Message = "Liaison interrompue.";
                ConfigurationMessage = "Brouillon conservé. Une opération interrompue peut avoir été partiellement appliquée ; vérifiez la configuration après reconnexion.";
            });
            SetConnecting(true);
            try
            {
                Message = automatic ? "Reconnexion au dernier contrôleur…" : "Connexion au contrôleur…";
                await transport.ConnectAsync(address, lifetime.Token, allowPairing: !automatic);
                ControllerName = string.IsNullOrWhiteSpace(transport.DeviceName) ? "Nom du contrôleur indisponible" : transport.DeviceName;
                // Le délai de découverte ne doit pas raccourcir les 20 s de chaque requête.
                lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
                _controller = new(session);
                Message = "Lecture des réglages…";
                var state = await _controller.SynchronizeAsync(lifetime.Token);
                await AcceptRemoteAsync(state, replaceDraft: Profiles.Count == 0);
                Connected = true;
                ShowConnection(false);
                _lastDevice = address;
                _reconnect.Succeeded();
                try { _preferences.Save(address); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Diagnostics.Record(ex);
                    Error = $"Connexion établie, mais mémorisation impossible : {ex.Message}";
                }
                Message = "Liaison Bluetooth chiffrée.";
                _ = PollAsync(_controller, session, lifetime.Token);
            }
            catch
            {
                Message = _reconnect.Paused
                    ? "Reconnexion automatique suspendue. Choisissez un contrôleur pour reprendre."
                    : "Contrôleur inaccessible. Vérifiez son alimentation et sa portée.";
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
            Error = $"Lecture du démarrage Windows impossible : {ex.Message}";
        }
        try { _lastDevice = _preferences.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Record(ex);
            Error = $"Préférence de connexion illisible : {ex.Message}";
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
                Error = ex.Message;
                ShowConnection(true);
                Message = _reconnect.Paused
                    ? "Reconnexion automatique suspendue. Choisissez un contrôleur pour reprendre."
                    : $"Reconnexion automatique • nouvelle tentative après {_reconnect.Delay.TotalSeconds:0} s.";
            });
        await loop.RunAsync(_applicationLifetime.Token);
    }

    private async Task<bool> TryAutoConnectAsync(ulong address, CancellationToken ct)
    {
        Busy = true;
        SetConnecting(true);
        Message = "Reconnexion au dernier contrôleur…";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            var paired = await WindowsBleTransport.IsPairedAsync(address, timeout.Token);
            if (_disposed || _reconnect.Paused) return false;
            if (!paired)
            {
                ShowConnection(true);
                Message = "Le dernier contrôleur n’est plus appairé dans Windows. Connexion manuelle requise.";
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
            Dispatch(() => { if (_session == session) Error = ex.Message; });
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
            ? $"Firmware {version}" : "Firmware ancien • version non publiée";
        Storage = $"RAM distante : {(status.Dirty ? "NON enregistrée" : "enregistrée / inchangée")} • " +
                  $"NVS : {TranslateStorage(status.Storage)} • disponible : {(status.StorageAvailable ? "oui" : "non")}";
        LastUpdate = $"Dernière mesure : {DateTime.Now:HH:mm:ss} • fonctionnement : {TimeSpan.FromMilliseconds(status.UptimeMs):g}";
        Changed(nameof(LastUpdate));
        foreach (var sensor in status.Sensors.OrderBy(s => s.Sensor))
            Sensors[sensor.Sensor - 1].Update(sensor.Temperature is { } t ? $"{t:F1} °C" : "Indisponible",
                $"{sensor.Millivolts:F1} mV • {TranslateFault(sensor.Fault)} • sécurité : {(sensor.Safety ? "ACTIVE" : "inactive")}",
                $"{TranslateFault(sensor.Fault)} • sécurité : {(sensor.Safety ? "ACTIVE" : "inactive")}");
        foreach (var fan in status.Fans.OrderBy(f => f.Fan))
            FanReadings[fan.Fan - 1].Update($"{fan.Rpm:N0} tr/min",
                $"{(fan.Mode == "auto" ? "Automatique" : "Fixe")} • fixe {fan.FixedDuty:F1} % • cible {fan.TargetDuty:F1} % • PWM {fan.AppliedDuty:F1} %\n" +
                $"Sortie PWM : {(fan.PwmOk ? "OK" : "DÉFAUT")} • boost : {(fan.Boost ? "actif" : "inactif")}",
                $"{(fan.Mode == "auto" ? "Automatique" : "Fixe")} • PWM {fan.AppliedDuty:F1} %" +
                (fan.PwmOk ? "" : " • DÉFAUT SORTIE PWM"));
    }

    private static string TranslateFault(string value) => value switch
    {
        "none" => "aucun défaut", "short_circuit" => "COURT-CIRCUIT", "open_circuit" => "CIRCUIT OUVERT",
        "out_of_range" => "HORS PLAGE", _ => $"défaut inconnu ({value})"
    };
    private static string TranslateStorage(string value) => value switch
    {
        "defaults" => "valeurs d'usine", "loaded" => "chargée", "saved" => "enregistrée",
        "invalid" => "INVALIDE", "unavailable" => "INDISPONIBLE", "write_error" => "ERREUR D'ÉCRITURE",
        "verify_error" => "ERREUR DE VÉRIFICATION", _ => value
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
        Error = "";
        Storage = "Déconnecté ; état distant non vérifié.";
        Message = "Reconnexion automatique suspendue. Choisissez un contrôleur pour reprendre.";
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
