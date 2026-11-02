using System.Collections.ObjectModel;
using System.Text.Json;
using FanController.App.Bluetooth;
using FanController.Core;
using Microsoft.UI.Dispatching;

namespace FanController.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private BleScanner? _scanner;
    private CancellationTokenSource? _scanCts, _connectionCts;
    private ProtocolSession? _session;
    private ControllerService? _controller;
    private RemoteConfiguration? _remote;
    private bool _busy, _scanning, _connected, _localDirty;
    private string _message = "Recherchez votre ESP32 puis connectez-vous. Aucune donnée simulée.";
    private string _error = "", _validation = "", _storage = "État distant inconnu.";
    private BleDeviceDisplay? _selectedDevice;
    private ulong _connectedAddress;
    private string _historySession = Guid.NewGuid().ToString("N");
    private bool _advancedMode;
    public bool AdvancedMode
    {
        get => _advancedMode;
        set
        {
            if (!Set(ref _advancedMode, value)) return;
            Changed(nameof(AdvancedVisibility));
            foreach (var sensor in Sensors) sensor.Advanced = value;
            foreach (var fan in FanReadings) fan.Advanced = value;
        }
    }
    public Microsoft.UI.Xaml.Visibility AdvancedVisibility => AdvancedMode
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
    public bool Connected { get => _connected; private set { Set(ref _connected, value); Changed(nameof(ConnectionLabel)); Changed(nameof(CanEdit)); RefreshCommands(); } }
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
        DisconnectCommand = Command(() => { Disconnect(); return Task.CompletedTask; }, () => _session is not null);
        ReadCommand = Command(() => Operation(async () =>
        {
            await AcceptRemoteAsync(await _controller!.SynchronizeAsync(_connectionCts!.Token), replaceDraft: false);
            Message = "Configuration distante relue ; brouillon conservé. Aucun changement réappliqué.";
        }), Ready);
        AdoptCommand = Command(() =>
        {
            ReplaceDraft(_remote!.Configuration);
            Message = "Brouillon remplacé par la dernière configuration lue, sans écriture sur l'ESP32.";
            return Task.CompletedTask;
        }, () => !Busy && _remote is not null);
        ApplyCommand = Command(() => Operation(async () =>
        {
            var draft = ReadDraft();
            await AcceptRemoteAsync(await _controller!.ApplyAsync(draft, _connectionCts!.Token), replaceDraft: true);
            Message = "Configuration appliquée en RAM. Cliquez sur Enregistrer NVS pour la rendre persistante.";
        }), () => Ready() && _localDirty && string.IsNullOrEmpty(ValidationMessage));
        SaveCommand = Command(() => Operation(async () =>
        {
            await AcceptRemoteAsync(await _controller!.SaveAsync(_connectionCts!.Token), replaceDraft: false);
            Message = "Configuration distante enregistrée en NVS et relue.";
        }), () => Ready() && !_localDirty);
        DefaultsCommand = Command(() => Operation(async () =>
        {
            await AcceptRemoteAsync(await _controller!.DefaultsAsync(_connectionCts!.Token), replaceDraft: true);
            Message = "Valeurs d'usine appliquées en RAM uniquement. Enregistrement NVS séparé.";
        }), Ready);
    }

    private AsyncCommand Command(Func<Task> action, Func<bool> enabled) => new(action, enabled, ex =>
    {
        Diagnostics.Record(ex);
        Error = ex.Message;
    });
    private bool Ready() => Connected && !Busy;
    private void RefreshCommands() { foreach (var command in Commands) command?.Refresh(); }
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
        _scanning = true;
        RefreshCommands();
        Devices.Clear();
        SelectedDevice = null;
        Error = "";
        Message = "Recherche BLE active pendant 15 secondes (annonces et scan responses)…";
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
        await Operation(async () =>
        {
            _scanCts?.Cancel();
            _scanner?.Dispose();
            _scanner = null;
            CloseSession();
            var address = SelectedDevice!.Address;
            _connectedAddress = address;
            _historySession = Guid.NewGuid().ToString("N");
            History.SelectDevice(address);
            var transport = new WindowsBleTransport();
            var session = new ProtocolSession(transport);
            _session = session;
            var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            _connectionCts = lifetime;
            session.Invalidated += ex => Dispatch(() =>
            {
                if (_session != session) return;
                lifetime.Cancel();
                Connected = false;
                Storage = "État distant incertain ; reconnexion et relecture obligatoires.";
                Error = ex.Message;
                Message = "Brouillon conservé. Une opération interrompue peut avoir été partiellement appliquée.";
            });
            try
            {
                Message = "Connexion BLE et abonnement aux notifications…";
                await transport.ConnectAsync(address, lifetime.Token);
                // Le délai de découverte ne doit pas raccourcir les 20 s de chaque requête.
                lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
                _controller = new(session);
                Message = "Liaison BLE établie ; lecture de la configuration et des mesures…";
                var state = await _controller.SynchronizeAsync(lifetime.Token);
                await AcceptRemoteAsync(state, replaceDraft: Profiles.Count == 0);
                Connected = true;
                Message = "Connecté et resynchronisé. Aucun brouillon local réappliqué automatiquement.";
                _ = PollAsync(_controller, session, lifetime.Token);
            }
            catch
            {
                Message = "Connexion ou lecture initiale impossible. Aucune mesure reçue ; consultez l’erreur et le journal.";
                CloseSession();
                throw;
            }
        });
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
                await History.RecordAsync(status, address, historySession, ct).ConfigureAwait(false);
                Dispatch(() => { if (_session == session && Connected) UpdateTelemetry(status); });
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
        RefreshCommands();
    }
    private void UpdateTelemetry(Telemetry status)
    {
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
        Connected = false;
        RefreshCommands();
    }
    private void Disconnect()
    {
        CloseSession();
        Error = "";
        Storage = "Déconnecté ; état distant non vérifié.";
        Message = "Déconnexion explicite. Brouillon conservé ; aucune réapplication à la reconnexion.";
    }
    public void Dispose()
    {
        _scanCts?.Cancel();
        _scanner?.Dispose();
        _scanner = null;
        CloseSession();
    }
}
