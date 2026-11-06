using FanController.App.Bluetooth;
using FanController.App.ViewModels;
using FanController.Core;
using Microsoft.UI.Dispatching;

namespace FanController.App.Services;

internal static class DesktopServices
{
    internal static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanController");

    internal static MainViewModel CreateViewModel(DispatcherQueue queue)
    {
        var dispatcher = new UiDispatcher(queue);
        var store = new HistoryStore(Path.Combine(DataDirectory, "history"));
        var history = new HistoryViewModel(dispatcher, store, Diagnostics.Record);
        var connection = new ControllerConnection(
            () => new WindowsBleTransport(), WindowsBleTransport.IsPairedAsync,
            new ConnectionPreferences(Path.Combine(DataDirectory, "last-device.txt")), store);

        return new(connection, history, dispatcher, new StartupSettings(), () => new BleScanner(), Diagnostics.Record,
            new DeviceNamePreferences(Path.Combine(DataDirectory, "device-names.json")));
    }

    private sealed class StartupSettings : IStartupSettings
    {
        public bool Enabled() => WindowsStartup.Enabled();
        public void SetEnabled(bool enabled) => WindowsStartup.SetEnabled(enabled);
    }
}
