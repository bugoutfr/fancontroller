namespace FanController.Core;

/// <summary>The stages of a controller connection, including automatic retries.</summary>
public enum ConnectionStage
{
    /// <summary>Discovery or pairing is in progress.</summary>
    Connecting,
    /// <summary>The initial configuration and telemetry are being read.</summary>
    Reading,
    /// <summary>The initial read succeeded and acquisition is active.</summary>
    Connected,
    /// <summary>The link was closed or invalidated.</summary>
    Disconnected,
    /// <summary>The remembered device needs manual pairing.</summary>
    Unpaired,
    /// <summary>An automatic connection failed and will be retried.</summary>
    Retrying
}

/// <summary>An immutable connection notification; events can arrive on a worker thread.</summary>
/// <param name="Stage">The current connection stage.</param>
/// <param name="Address">The controller address.</param>
/// <param name="Name">The discovered controller name.</param>
/// <param name="Automatic">Whether this is an automatic connection attempt.</param>
/// <param name="InitialState">The configuration and telemetry from the successful initial read.</param>
/// <param name="Error">The failure, if any.</param>
/// <param name="RetryDelay">The delay before another automatic attempt.</param>
public sealed record ConnectionUpdate(
    ConnectionStage Stage, ulong Address, string? Name = null, bool Automatic = false,
    (RemoteConfiguration Config, Telemetry Status)? InitialState = null,
    Exception? Error = null, TimeSpan RetryDelay = default);

/// <summary>
/// Owns BLE sessions, automatic reconnection and acquisition. Interrupted writes are never replayed.
/// All operations and shutdown are awaited before session resources are released.
/// </summary>
public sealed class ControllerConnection(
    Func<IControllerTransport> transportFactory,
    Func<ulong, CancellationToken, Task<bool>> isPaired,
    ConnectionPreferences preferences,
    IHistoryStore history) : IAsyncDisposable
{
    private sealed class ActiveConnection
    {
        public ActiveConnection(IControllerTransport transport, CancellationTokenSource lifetime, ulong address)
        {
            Transport = transport;
            Lifetime = lifetime;
            Address = address;
            Session = new(transport);
            Controller = new(Session);
        }

        public IControllerTransport Transport
        {
            get;
        }
        public CancellationTokenSource Lifetime
        {
            get;
        }
        public ulong Address
        {
            get;
        }
        public string HistorySession { get; } = Guid.NewGuid().ToString("N");
        public ProtocolSession Session
        {
            get;
        }
        public ControllerService Controller
        {
            get;
        }
        public Task Polling { get; set; } = Task.CompletedTask;
    }

    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ReconnectPolicy _reconnect = new();
    private ActiveConnection? _current;
    private CancellationTokenSource? _attemptCts;
    private Task _reconnection = Task.CompletedTask;
    private Task? _shutdown;
    private ulong? _lastDevice;
    private bool _started, _disposed;
    private (ulong Address, string Hash)? _expectedFirmware;

    /// <summary>Whether the active transport exposes the optional encrypted OTA channel.</summary>
    public bool FirmwareUpdateSupported => _current?.Transport is IFirmwareTransport { FirmwareUpdateSupported: true };

    /// <summary>Reports connection transitions. Subscribers must marshal presentation updates to the UI.</summary>
    public event Action<ConnectionUpdate>? Changed;

    /// <summary>Reports polling measurements after a successful protocol read.</summary>
    public event Action<Telemetry>? TelemetryReceived;

    /// <summary>Reports successfully persisted measurements for a controller.</summary>
    public event Action<ulong>? HistoryWritten;

    /// <summary>Reports disk errors without interrupting BLE control.</summary>
    public event Action<Exception>? HistoryWriteFailed;

    /// <summary>Reports failure to read or save the remembered controller address.</summary>
    public event Action<Exception>? PreferenceFailed;

    /// <summary>Reports a pending OTA image verified on the actual running partition after reconnection.</summary>
    public event Action<ulong>? FirmwareVerified;

    /// <summary>Gets whether explicit disconnect or scanning has paused reconnection.</summary>
    public bool ReconnectionPaused => _reconnect.Paused;

    /// <summary>Gets whether there is a current or remembered controller that can be disconnected.</summary>
    public bool CanDisconnect => !ReconnectionPaused && (_current is not null || _lastDevice is not null);

    /// <summary>Loads the remembered address and starts the owned reconnection loop once.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            return;
        _started = true;

        try
        {
            _lastDevice = preferences.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PreferenceFailed?.Invoke(ex);
        }

        _reconnection = ReconnectAsync();
    }

    /// <summary>Explicitly connects to an address, allowing pairing, then rereads actual controller state.</summary>
    /// <exception cref="IOException">Connection or synchronization fails; the session is closed.</exception>
    public async Task ConnectAsync(ulong address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _reconnect.Resume();
        await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            await ConnectCoreAsync(address, automatic: false).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Pauses retries, cancels current work and waits for acquisition to stop.</summary>
    public async Task DisconnectAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _reconnect.Pause();
        CancelConnection();
        await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads actual configuration and telemetry without writing to the controller.</summary>
    public Task<(RemoteConfiguration Config, Telemetry Status)> SynchronizeAsync() =>
        RunAsync((controller, ct) => controller.SynchronizeAsync(ct));

    /// <summary>Applies a validated draft to RAM; it does not save and is not atomic.</summary>
    public Task<(RemoteConfiguration Config, Telemetry Status)> ApplyAsync(Configuration draft) =>
        RunAsync((controller, ct) => controller.ApplyAsync(draft, ct));

    /// <summary>Saves current remote RAM settings and rereads actual state.</summary>
    public Task<(RemoteConfiguration Config, Telemetry Status)> SaveAsync() =>
        RunAsync((controller, ct) => controller.SaveAsync(ct));

    /// <summary>Loads factory defaults into remote RAM without persisting them.</summary>
    public Task<(RemoteConfiguration Config, Telemetry Status)> DefaultsAsync() =>
        RunAsync((controller, ct) => controller.DefaultsAsync(ct));

    /// <summary>Captures TACH diagnostics on the current session without replay after reconnect.</summary>
    /// <param name="seconds">Either 30 or 60 seconds.</param>
    /// <param name="progress">Receives elapsed acquisition seconds.</param>
    /// <param name="ct">Cancels acquisition.</param>
    /// <returns>Consecutive windows from a single controller session.</returns>
    public async Task<IReadOnlyList<TachWindow>> CaptureTachAsync(int seconds,
        IProgress<double>? progress, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            var connection = _current ?? throw new IOException("No controller is connected.");
            using var capture = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, connection.Lifetime.Token);
            return await connection.Controller.CaptureTachAsync(seconds, progress, capture.Token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Uploads local firmware, asks before activation and reconnects to verify the actual running image.</summary>
    /// <param name="path">Local application .bin file.</param>
    /// <param name="progress">Acknowledged transfer percentage.</param>
    /// <param name="confirmActivation">Confirmation after staging; must honor cancellation during shutdown.</param>
    /// <param name="ct">Cancels loading, transfer, confirmation or reconnection.</param>
    /// <param name="transferProgress">Optional upload-only timing and acknowledged bytes.</param>
    /// <returns>True when the new running image was verified; false when activation was declined.</returns>
    public async Task<bool> UpdateFirmwareAsync(string path, IProgress<double>? progress,
        Func<CancellationToken, Task<bool>> confirmActivation, CancellationToken ct = default,
        IProgress<FirmwareTransferProgress>? transferProgress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var image = await FirmwareImage.LoadAsync(path, lifetime.Token).ConfigureAwait(false);
        await _gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            var connection = _current ?? throw new IOException("No controller is connected.");
            if (connection.Transport is not IFirmwareTransport { FirmwareUpdateSupported: true } ota)
                throw new IOException("Install OTA-capable firmware over USB first (1.2.0 or later).");
            using var transfer = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, connection.Lifetime.Token);
            var activated = await connection.Controller.UpdateFirmwareAsync(ota, image, progress,
                confirmActivation, transfer.Token, transferProgress).ConfigureAwait(false);
            if (!activated)
                return false;
            _expectedFirmware = (connection.Address, image.ImageSha256);
            await CloseCoreAsync().ConfigureAwait(false);
            await ReconnectFirmwareAsync(connection.Address, lifetime.Token).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task ReconnectFirmwareAsync(ulong address, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        Exception? lastError = null;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token).ConfigureAwait(false);
                try
                {
                    await ConnectCoreAsync(address, automatic: true, deadline.Token).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (!deadline.IsCancellationRequested &&
                    (ex is IOException || ex is OperationCanceledException))
                {
                    lastError = ex;
                    Changed?.Invoke(new(ConnectionStage.Retrying, address, Automatic: true,
                        Error: ex, RetryDelay: TimeSpan.FromSeconds(2)));
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new IOException("Firmware activation was acknowledged, but the running image could not be verified within 90 seconds. Reconnect to complete verification.", lastError);
        }
    }

    private async Task<(RemoteConfiguration Config, Telemetry Status)> RunAsync(
        Func<ControllerService, CancellationToken, Task<(RemoteConfiguration Config, Telemetry Status)>> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            var connection = _current ?? throw new IOException("No controller is connected.");
            var state = await action(connection.Controller, connection.Lifetime.Token).ConfigureAwait(false);
            await RecordAsync(connection, state.Status).ConfigureAwait(false);
            return state;
        }
        finally { _gate.Release(); }
    }

    private async Task ConnectCoreAsync(ulong address, bool automatic, CancellationToken ct = default)
    {
        await CloseCoreAsync().ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var connection = new ActiveConnection(
            transportFactory(), CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, ct), address);
        lock (_sync)
            _current = connection;
        connection.Session.Invalidated += error => OnInvalidated(connection, error);
        Changed?.Invoke(new(ConnectionStage.Connecting, address, Automatic: automatic));

        try
        {
            connection.Lifetime.CancelAfter(TimeSpan.FromSeconds(35));
            await connection.Transport.ConnectAsync(address, connection.Lifetime.Token, !automatic).ConfigureAwait(false);
            connection.Lifetime.CancelAfter(Timeout.InfiniteTimeSpan);

            Changed?.Invoke(new(ConnectionStage.Reading, address, connection.Transport.DeviceName, automatic));
            var state = await connection.Controller.SynchronizeAsync(connection.Lifetime.Token).ConfigureAwait(false);
            var firmwareVerified = false;
            if (_expectedFirmware is { } expected && expected.Address == address)
            {
                await connection.Controller.VerifyFirmwareAsync(expected.Hash, connection.Lifetime.Token).ConfigureAwait(false);
                _expectedFirmware = null;
                firmwareVerified = true;
            }
            await RecordAsync(connection, state.Status).ConfigureAwait(false);
            connection.Lifetime.Token.ThrowIfCancellationRequested();
            _lastDevice = address;
            _reconnect.Succeeded();

            Changed?.Invoke(new(ConnectionStage.Connected, address, connection.Transport.DeviceName, automatic, state));
            if (firmwareVerified)
                FirmwareVerified?.Invoke(address);
            try
            {
                preferences.Save(address);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PreferenceFailed?.Invoke(ex);
            }

            connection.Polling = PollAsync(connection);
        }
        catch
        {
            await CloseCoreAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void OnInvalidated(ActiveConnection connection, Exception error)
    {
        lock (_sync)
        {
            if (_current != connection)
                return;
            connection.Lifetime.Cancel();
        }
        Changed?.Invoke(new(ConnectionStage.Disconnected, connection.Address, Error: error));
    }

    private async Task PollAsync(ActiveConnection connection)
    {
        var ct = connection.Lifetime.Token;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                var status = await connection.Controller.StatusAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                TelemetryReceived?.Invoke(status);
                await RecordAsync(connection, status).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            connection.Session.Invalidate(ex);
        }
    }

    private async Task RecordAsync(ActiveConnection connection, Telemetry telemetry)
    {
        try
        {
            var sample = HistorySample.FromTelemetry(DateTimeOffset.UtcNow, telemetry)
                with
            {
                SessionId = connection.HistorySession
            };
            await history.AppendAsync(connection.Address, sample, connection.Lifetime.Token).ConfigureAwait(false);
            HistoryWritten?.Invoke(connection.Address);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HistoryWriteFailed?.Invoke(ex);
        }
    }

    private Task ReconnectAsync() => new AutoReconnectLoop(
        _reconnect, EligibleDevice, TryReconnectAsync,
        error => Changed?.Invoke(new(ConnectionStage.Retrying, _lastDevice ?? 0, Automatic: true,
            Error: error, RetryDelay: _reconnect.Delay))).RunAsync(_lifetime.Token);

    private ulong? EligibleDevice() =>
        !_disposed && _gate.CurrentCount == 1 && (_current is null || !_current.Session.IsValid)
            ? _lastDevice : null;

    private async Task<bool> TryReconnectAsync(ulong address, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            lock (_sync)
            {
                if (_reconnect.Paused)
                    return false;
                _attemptCts = timeout;
            }

            Changed?.Invoke(new(ConnectionStage.Connecting, address, Automatic: true));
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            if (!await isPaired(address, timeout.Token).ConfigureAwait(false))
            {
                Changed?.Invoke(new(ConnectionStage.Unpaired, address, Automatic: true));
                return false;
            }

            timeout.Token.ThrowIfCancellationRequested();
            await ConnectCoreAsync(address, automatic: true).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (_reconnect.Paused && !ct.IsCancellationRequested)
        {
            Changed?.Invoke(new(ConnectionStage.Disconnected, address));
            return false;
        }
        finally
        {
            lock (_sync)
                _attemptCts = null;
            _gate.Release();
        }
    }

    private void CancelConnection()
    {
        lock (_sync)
        {
            _attemptCts?.Cancel();
            _current?.Lifetime.Cancel();
        }
    }

    private async Task CloseCoreAsync()
    {
        ActiveConnection? connection;
        lock (_sync)
        {
            connection = _current;
            _current = null;
            connection?.Lifetime.Cancel();
        }
        if (connection is null)
            return;

        await connection.Polling.ConfigureAwait(false);
        connection.Session.Dispose();
        connection.Controller.Dispose();
        connection.Lifetime.Dispose();
        Changed?.Invoke(new(ConnectionStage.Disconnected, connection.Address));
    }

    /// <summary>Cancels and awaits reconnection, requests and polling before disposing owned resources.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_sync)
            return new(_shutdown ??= ShutdownAsync());
    }

    private async Task ShutdownAsync()
    {
        _disposed = true;
        _reconnect.Pause();
        _lifetime.Cancel();
        CancelConnection();
        await _reconnection.ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _lifetime.Dispose();
        }
    }
}
