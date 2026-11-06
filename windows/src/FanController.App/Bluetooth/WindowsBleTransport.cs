using FanController.Core;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using Windows.Devices.Enumeration;

namespace FanController.App.Bluetooth;

/// <summary>Transport UART BLE Windows à écritures confirmées et notifications chiffrées.</summary>
public sealed class WindowsBleTransport : IControllerTransport, IFirmwareTransport
{
    private static readonly Guid RxId = new("6e400002-b5a3-f393-e0a9-e50e24dcca9e");
    private static readonly Guid TxId = new("6e400003-b5a3-f393-e0a9-e50e24dcca9e");
    private static readonly Guid OtaId = new("6e400004-b5a3-f393-e0a9-e50e24dcca9e");
    private readonly object _sync = new();
    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _rx, _tx;
    private GattCharacteristic? _ota;
    private TaskCompletionSource<uint>? _firmwareAck;
    private readonly SemaphoreSlim _firmwareGate = new(1, 1);
    private bool _closed, _ready;

    /// <inheritdoc />
    public bool FirmwareUpdateSupported => _ota is not null;

    /// <inheritdoc />
    public int FirmwareBlockSize => Math.Clamp((_service?.Session.MaxPduSize ?? 23) - 7, 16, 508);

    /// <inheritdoc />
    public string? DeviceName
    {
        get; private set;
    }

    /// <inheritdoc />
    public event Action<byte[]>? BytesReceived;

    /// <inheritdoc />
    public event Action<Exception>? Disconnected;

    /// <summary>Vérifie si Windows connaît un appairage pour le contrôleur.</summary>
    /// <param name="address">Adresse Bluetooth du contrôleur.</param>
    /// <param name="ct">Jeton d'annulation de la recherche.</param>
    /// <returns>True si le périphérique est accessible et déjà appairé.</returns>
    /// <exception cref="OperationCanceledException">La recherche est annulée.</exception>
    public static async Task<bool> IsPairedAsync(ulong address, CancellationToken ct)
    {
        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(ct);
        return device?.DeviceInformation.Pairing.IsPaired == true;
    }

    /// <inheritdoc />
    public Task ConnectAsync(ulong address, CancellationToken cancellationToken) =>
        ConnectAsync(address, cancellationToken, allowPairing: true);

    /// <inheritdoc />
    public async Task ConnectAsync(ulong address, CancellationToken cancellationToken, bool allowPairing)
    {
        var stage = "Opening BLE device";
        try
        {
            var device = await OpenDeviceAsync(address, cancellationToken);

            stage = "Windows pairing (hold BOOT for 2 seconds, then release, for a new PC)";
            await PairDeviceAsync(device, allowPairing, cancellationToken);

            stage = "Discovering GATT services";
            await DiscoverServiceAsync(device, cancellationToken);

            stage = "Discovering RX/TX characteristics";
            var tx = await DiscoverCharacteristicsAsync(cancellationToken);

            // Impérativement avant la première écriture RX.
            stage = "Subscribing to TX notifications";
            var subscription = await tx.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(cancellationToken);
            Check(subscription.Status, "Subscribing to TX notifications", subscription.ProtocolError);
            if (_ota is { } ota)
            {
                var otaSubscription = await ota.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(cancellationToken);
                Check(otaSubscription.Status, "Subscribing to OTA acknowledgments", otaSubscription.ProtocolError);
            }
            lock (_sync)
            {
                if (_closed || device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                    throw new IOException("BLE connection was interrupted.");
                _ready = true;
            }
        }
        catch (COMException ex)
        {
            Disconnect();
            var advice = ex.HResult == unchecked((int)0x80070016)
                ? " Windows rejected GATT discovery (ERROR_BAD_COMMAND). Close other BLE clients, restart the ESP32, " +
                  "and remove the device from Windows Bluetooth settings before retrying if it is already paired."
                : " Check Bluetooth radio, range, and Windows permissions.";
            throw new IOException($"{stage}: Windows error 0x{ex.HResult:X8}.{advice}", ex);
        }
        catch
        {
            Disconnect();
            throw;
        }
    }

    private async Task<BluetoothLEDevice> OpenDeviceAsync(ulong address, CancellationToken cancellationToken)
    {
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(cancellationToken)
            ?? throw new IOException("BLE device not found. Run a new scan.");
        DeviceName = device.Name;
        lock (_sync)
        {
            if (_closed)
            {
                device.Dispose();
                throw new IOException("Connection was cancelled.");
            }

            _device = device;
            device.ConnectionStatusChanged += StatusChanged;
        }

        return device;
    }

    private static async Task PairDeviceAsync(BluetoothLEDevice device, bool allowPairing,
        CancellationToken cancellationToken)
    {
        if (!device.DeviceInformation.Pairing.IsPaired)
        {
            if (!allowPairing)
                throw new IOException("The remembered controller is no longer paired. Pair it manually with BOOT.");

            var pairing = device.DeviceInformation.Pairing.Custom;
            void Confirm(DeviceInformationCustomPairing _, DevicePairingRequestedEventArgs args)
            {
                if (args.PairingKind == DevicePairingKinds.ConfirmOnly)
                    args.Accept();
            }

            pairing.PairingRequested += Confirm;
            try
            {
                var result = await pairing.PairAsync(DevicePairingKinds.ConfirmOnly,
                    DevicePairingProtectionLevel.Encryption).AsTask(cancellationToken);
                if (result.Status is not (DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired))
                    throw new IOException($"Pairing was rejected: {result.Status}. Open the ESP32 BOOT pairing window.");
                // The live GATT protection requirements below enforce encryption;
                // ProtectionLevelUsed can under-report it even for a successful pairing.
            }
            finally
            {
                pairing.PairingRequested -= Confirm;
            }
        }
    }

    private async Task DiscoverServiceAsync(BluetoothLEDevice device, CancellationToken cancellationToken)
    {
        // La découverte filtrée par UUID peut échouer avec ERROR_BAD_COMMAND sur Windows.
        var services = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
        Check(services.Status, "Discovering UART service");
        var uart = services.Services.FirstOrDefault(service => service.Uuid == AdvertisementIndex.ServiceId);
        foreach (var service in services.Services)
            if (service != uart)
                service.Dispose();
        if (uart is null)
            throw new IOException("UART service is missing. Check the ESP32 firmware.");

        lock (_sync)
        {
            if (_closed)
            {
                uart.Dispose();
                throw new IOException("Connection was cancelled.");
            }

            _service = uart;
        }
    }

    private async Task<GattCharacteristic> DiscoverCharacteristicsAsync(CancellationToken cancellationToken)
    {
        var chars = await _service!.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
        Check(chars.Status, "Discovering characteristics");
        var rx = chars.Characteristics.SingleOrDefault(c => c.Uuid == RxId)
            ?? throw new IOException("RX characteristic is missing.");
        var tx = chars.Characteristics.SingleOrDefault(c => c.Uuid == TxId)
            ?? throw new IOException("TX characteristic is missing.");
        if (!rx.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write) ||
            !tx.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
            throw new IOException("WriteWithResponse/Notify properties are missing.");

        rx.ProtectionLevel = GattProtectionLevel.EncryptionRequired;
        tx.ProtectionLevel = GattProtectionLevel.EncryptionRequired;
        var ota = chars.Characteristics.SingleOrDefault(c => c.Uuid == OtaId);
        if (ota is not null)
        {
            if (!ota.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write) ||
                !ota.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
                throw new IOException("OTA WriteWithResponse/Notify properties are missing.");
            ota.ProtectionLevel = GattProtectionLevel.EncryptionRequired;
        }
        lock (_sync)
        {
            if (_closed)
                throw new IOException("Connection was cancelled.");
            _rx = rx;
            _tx = tx;
            _ota = ota;
            tx.ValueChanged += ValueChanged;
            if (ota is not null)
                ota.ValueChanged += FirmwareValueChanged;
        }

        return tx;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">Le fragment contient moins de 1 ou plus de 20 octets.</exception>
    public async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        GattCharacteristic rx;
        lock (_sync)
        {
            if (_closed || !_ready || _rx is null)
                throw new IOException("BLE is not connected or TX notifications are not subscribed.");
            rx = _rx;
        }

        if (bytes.Length is < 1 or > 20)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        using var writer = new DataWriter();
        writer.WriteBytes(bytes.ToArray());
        var result = await rx.WriteValueWithResultAsync(writer.DetachBuffer(),
            GattWriteOption.WriteWithResponse).AsTask(cancellationToken);
        Check(result.Status, "Writing RX", result.ProtocolError);
    }

    private void ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            using var reader = DataReader.FromBuffer(args.CharacteristicValue);
            var bytes = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(bytes);
            BytesReceived?.Invoke(bytes);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    /// <inheritdoc />
    public async Task<uint> WriteFirmwareBlockAsync(uint offset, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (bytes.Length < 1 || bytes.Length > FirmwareBlockSize)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        await _firmwareGate.WaitAsync(ct);
        try
        {
            GattCharacteristic ota;
            TaskCompletionSource<uint> pending;
            lock (_sync)
            {
                if (_closed || !_ready || _ota is null)
                    throw new IOException("The connected firmware does not support Bluetooth OTA.");
                ota = _ota;
                pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _firmwareAck = pending;
            }
            _ = pending.Task.ContinueWith(t => { _ = t.Exception; },
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var packet = new byte[bytes.Length + 4];
                BinaryPrimitives.WriteUInt32LittleEndian(packet, offset);
                bytes.CopyTo(packet.AsMemory(4));
                using var writer = new DataWriter();
                writer.WriteBytes(packet);
                var writeOption = ota.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
                    ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse;
                var result = await ota.WriteValueWithResultAsync(writer.DetachBuffer(),
                    writeOption).AsTask(deadline.Token);
                Check(result.Status, "Writing OTA block", result.ProtocolError);
                var accepted = await pending.Task.WaitAsync(deadline.Token);
                if (accepted != offset + bytes.Length)
                    throw new InvalidDataException("OTA acknowledgment offset does not match the written block.");
                return accepted;
            }
            catch (Exception ex)
            {
                var error = ex is OperationCanceledException && !ct.IsCancellationRequested
                    ? new TimeoutException("OTA acknowledgment timed out; the transfer will not be replayed.", ex) : ex;
                Fail(error);
                throw error;
            }
            finally
            {
                lock (_sync)
                    _firmwareAck = null;
            }
        }
        finally { _firmwareGate.Release(); }
    }

    private void FirmwareValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            using var reader = DataReader.FromBuffer(args.CharacteristicValue);
            var bytes = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(bytes);
            lock (_sync)
            {
                if (_closed)
                    return;
                if (bytes.Length != 5 || _firmwareAck is null || _firmwareAck.Task.IsCompleted)
                    throw new InvalidDataException("Unexpected OTA acknowledgment.");
                if (bytes[0] != 0)
                    throw new IOException($"ESP32 rejected OTA block (status {bytes[0]}).");
                _firmwareAck.TrySetResult(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(1)));
            }
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void StatusChanged(BluetoothLEDevice sender, object args)
    {
        bool ready;
        lock (_sync)
            ready = _ready && !_closed;
        if (ready && sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            Fail(new IOException("BLE link lost. Reconnect to resynchronize."));
    }
    private void Fail(Exception ex)
    {
        Disconnect();
        Disconnected?.Invoke(ex);
    }

    private static void Check(GattCommunicationStatus status, string action, byte? protocolError = null)
    {
        if (status != GattCommunicationStatus.Success)
        {
            var detail = protocolError is { } code ? $" (ATT 0x{code:X2} : {AttError(code)})" : "";
            throw new IOException($"{action}: {status}{detail}. Check the connection and pairing keys on both devices.");
        }

    }
    private static string AttError(byte code) => code switch
    {
        0x01 => "invalid handle; GATT services may be stale",
        0x03 => "write not permitted",
        0x05 => "insufficient authentication",
        0x08 => "insufficient authorization",
        0x0C => "encryption key size is insufficient",
        0x0F => "link is not encrypted",
        _ => "GATT server error"
    };
    /// <inheritdoc />
    public void Disconnect()
    {
        lock (_sync)
        {
            if (_closed)
                return;
            _closed = true;
            _ready = false;
            if (_tx is not null)
                _tx.ValueChanged -= ValueChanged;
            if (_device is not null)
                _device.ConnectionStatusChanged -= StatusChanged;
            if (_ota is not null)
                _ota.ValueChanged -= FirmwareValueChanged;
            _firmwareAck?.TrySetException(new IOException("BLE disconnected during OTA."));
            _ota = null;
            _rx = _tx = null;
            _service?.Dispose();
            _service = null;
            _device?.Dispose();
            _device = null;
        }
    }

    /// <inheritdoc />
    public void Dispose() => Disconnect();
}
