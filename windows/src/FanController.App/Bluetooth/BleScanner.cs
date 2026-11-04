using FanController.Core;
using FanController.App.Localization;
using Windows.Devices.Bluetooth.Advertisement;

namespace FanController.App.Bluetooth;

public sealed class BleScanner : IDisposable
{
    private readonly BluetoothLEAdvertisementWatcher _watcher = new() { ScanningMode = BluetoothLEScanningMode.Active };
    private readonly AdvertisementIndex _index = new();
    private readonly object _sync = new();
    public event Action<BleCandidate>? Found;
    public event Action<string>? Failed;
    public BleScanner()
    {
        _watcher.Received += Received;
        _watcher.Stopped += Stopped;
    }
    public void Start() => _watcher.Start();
    private void Received(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        BleCandidate? candidate;
        lock (_sync)
            candidate = _index.Add(args.BluetoothAddress, args.Advertisement.LocalName,
                args.Advertisement.ServiceUuids.Contains(AdvertisementIndex.ServiceId), args.RawSignalStrengthInDBm);
        if (candidate is not null) Found?.Invoke(candidate);
    }
    private void Stopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (args.Error != Windows.Devices.Bluetooth.BluetoothError.Success)
            Failed?.Invoke(Localizer.Format("Ble.ScanStopped", "BLE scan stopped: {0}. Check Bluetooth radio and Windows permissions.", args.Error));
    }
    public void Dispose()
    {
        _watcher.Received -= Received;
        _watcher.Stopped -= Stopped;
        _watcher.Stop();
    }
}
