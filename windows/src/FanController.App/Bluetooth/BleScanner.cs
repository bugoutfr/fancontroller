using FanController.Core;
using FanController.App.Localization;
using FanController.App.Services;
using Windows.Devices.Bluetooth.Advertisement;

namespace FanController.App.Bluetooth;

/// <summary>Découvre les contrôleurs BLE et regroupe leurs annonces par adresse.</summary>
public sealed class BleScanner : IBleScanner
{
    private readonly BluetoothLEAdvertisementWatcher _watcher = new() { ScanningMode = BluetoothLEScanningMode.Active };
    private readonly AdvertisementIndex _index = new(() =>
        Localizer.Get("Connection.ControllerPendingName", "UART controller (pending name)"));
    private readonly object _sync = new();

    /// <inheritdoc />
    public event Action<BleCandidate>? Found;

    /// <inheritdoc />
    public event Action<string>? Failed;

    /// <summary>Crée un observateur actif et s'abonne à ses événements.</summary>
    public BleScanner()
    {
        _watcher.Received += Received;
        _watcher.Stopped += Stopped;
    }

    /// <inheritdoc />
    public void Start() => _watcher.Start();

    private void Received(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        BleCandidate? candidate;
        lock (_sync)
            candidate = _index.Add(args.BluetoothAddress, args.Advertisement.LocalName,
                args.Advertisement.ServiceUuids.Contains(AdvertisementIndex.ServiceId), args.RawSignalStrengthInDBm);
        if (candidate is not null)
            Found?.Invoke(candidate);
    }

    private void Stopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (args.Error != Windows.Devices.Bluetooth.BluetoothError.Success)
            Failed?.Invoke(Localizer.Format("Ble.ScanStopped", "BLE scan stopped: {0}. Check Bluetooth radio and Windows permissions.", args.Error));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _watcher.Received -= Received;
        _watcher.Stopped -= Stopped;
        _watcher.Stop();
    }
}
