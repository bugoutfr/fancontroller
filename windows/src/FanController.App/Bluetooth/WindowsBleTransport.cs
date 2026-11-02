using FanController.Core;
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using Windows.Devices.Enumeration;

namespace FanController.App.Bluetooth;

public sealed class WindowsBleTransport : IBleTransport
{
    private static readonly Guid RxId = new("6e400002-b5a3-f393-e0a9-e50e24dcca9e");
    private static readonly Guid TxId = new("6e400003-b5a3-f393-e0a9-e50e24dcca9e");
    private readonly object _sync = new();
    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _rx, _tx;
    private bool _closed, _ready;
    public event Action<byte[]>? BytesReceived;
    public event Action<Exception>? Disconnected;

    public async Task ConnectAsync(ulong address, CancellationToken cancellationToken)
    {
        var stage = "Ouverture de l’appareil BLE";
        try
        {
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(cancellationToken)
                ?? throw new IOException("Appareil BLE introuvable. Relancez une recherche.");
            lock (_sync)
            {
                if (_closed) { device.Dispose(); throw new IOException("Connexion annulée."); }
                _device = device;
                device.ConnectionStatusChanged += StatusChanged;
            }
            stage = "Appairage Windows (maintenir BOOT 2 secondes puis relâcher pour un nouveau PC)";
            if (!device.DeviceInformation.Pairing.IsPaired)
            {
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
                        throw new IOException($"Appairage refusé : {result.Status}. Ouvrez la fenêtre BOOT sur l’ESP32.");
                    // The live GATT protection requirements below enforce encryption;
                    // ProtectionLevelUsed can under-report it even for a successful pairing.
                }
                finally { pairing.PairingRequested -= Confirm; }
            }
            stage = "Découverte des services GATT";
            // La découverte filtrée par UUID peut échouer avec ERROR_BAD_COMMAND sur Windows.
            var services = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
            Check(services.Status, "Découverte du service UART");
            var uart = services.Services.FirstOrDefault(service => service.Uuid == AdvertisementIndex.ServiceId);
            foreach (var service in services.Services)
                if (service != uart) service.Dispose();
            if (uart is null) throw new IOException("Service UART absent. Vérifiez le firmware de l’ESP32.");
            lock (_sync)
            {
                if (_closed)
                {
                    uart.Dispose();
                    throw new IOException("Connexion annulée.");
                }
                _service = uart;
            }
            stage = "Découverte des caractéristiques RX/TX";
            var chars = await _service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
            Check(chars.Status, "Découverte des caractéristiques");
            var rx = chars.Characteristics.SingleOrDefault(c => c.Uuid == RxId)
                ?? throw new IOException("Caractéristique RX absente.");
            var tx = chars.Characteristics.SingleOrDefault(c => c.Uuid == TxId)
                ?? throw new IOException("Caractéristique TX absente.");
            if (!rx.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write) ||
                !tx.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
                throw new IOException("Propriétés WriteWithResponse/Notify absentes.");
            rx.ProtectionLevel = GattProtectionLevel.EncryptionRequired;
            tx.ProtectionLevel = GattProtectionLevel.EncryptionRequired;
            lock (_sync)
            {
                if (_closed) throw new IOException("Connexion annulée.");
                _rx = rx;
                _tx = tx;
                tx.ValueChanged += ValueChanged;
            }
            // Impérativement avant la première écriture RX.
            stage = "Abonnement aux notifications TX";
            var subscription = await tx.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(cancellationToken);
            Check(subscription.Status, "Abonnement aux notifications TX", subscription.ProtocolError);
            lock (_sync)
            {
                if (_closed || device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                    throw new IOException("Connexion BLE interrompue.");
                _ready = true;
            }
        }
        catch (COMException ex)
        {
            Disconnect();
            var advice = ex.HResult == unchecked((int)0x80070016)
                ? " Windows a refusé la découverte GATT (ERROR_BAD_COMMAND). Fermez les autres clients BLE, " +
                  "redémarrez l’ESP32 et, s’il est enregistré, supprimez l’appareil des paramètres Bluetooth avant de réessayer."
                : " Vérifiez la radio Bluetooth, la portée et les autorisations Windows.";
            throw new IOException($"{stage} : erreur Windows 0x{ex.HResult:X8}.{advice}", ex);
        }
        catch { Disconnect(); throw; }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        GattCharacteristic rx;
        lock (_sync)
        {
            if (_closed || !_ready || _rx is null) throw new IOException("BLE non connecté ou TX non abonné.");
            rx = _rx;
        }
        if (bytes.Length is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(bytes));
        using var writer = new DataWriter();
        writer.WriteBytes(bytes.ToArray());
        var result = await rx.WriteValueWithResultAsync(writer.DetachBuffer(),
            GattWriteOption.WriteWithResponse).AsTask(cancellationToken);
        Check(result.Status, "Écriture RX", result.ProtocolError);
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
        catch (Exception ex) { Fail(ex); }
    }
    private void StatusChanged(BluetoothLEDevice sender, object args)
    {
        bool ready;
        lock (_sync) ready = _ready && !_closed;
        if (ready && sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            Fail(new IOException("Liaison BLE perdue. Reconnectez-vous pour resynchroniser."));
    }
    private void Fail(Exception ex) { Disconnect(); Disconnected?.Invoke(ex); }
    private static void Check(GattCommunicationStatus status, string action, byte? protocolError = null)
    {
        if (status != GattCommunicationStatus.Success)
        {
            var detail = protocolError is { } code ? $" (ATT 0x{code:X2} : {AttError(code)})" : "";
            throw new IOException($"{action} : {status}{detail}. Vérifiez la liaison et les clés d’appairage des deux côtés.");
        }
    }
    private static string AttError(byte code) => code switch
    {
        0x01 => "handle invalide ; services GATT potentiellement périmés",
        0x03 => "écriture non autorisée",
        0x05 => "authentification insuffisante",
        0x08 => "autorisation insuffisante",
        0x0C => "taille de clé de chiffrement insuffisante",
        0x0F => "liaison non chiffrée",
        _ => "erreur du serveur GATT"
    };
    public void Disconnect()
    {
        lock (_sync)
        {
            if (_closed) return;
            _closed = true;
            _ready = false;
            if (_tx is not null) _tx.ValueChanged -= ValueChanged;
            if (_device is not null) _device.ConnectionStatusChanged -= StatusChanged;
            _rx = _tx = null;
            _service?.Dispose();
            _service = null;
            _device?.Dispose();
            _device = null;
        }
    }
    public void Dispose() => Disconnect();
}
