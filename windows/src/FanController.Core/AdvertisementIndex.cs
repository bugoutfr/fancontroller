namespace FanController.Core;

/// <summary>A merged BLE advertisement.</summary>
/// <param name="Address">The Bluetooth address.</param>
/// <param name="Name">The advertised name or caller-provided placeholder.</param>
/// <param name="Rssi">The RSSI received by Windows while scanning.</param>
public sealed record BleCandidate(ulong Address, string Name, short Rssi)
{
    /// <summary>A display label distinguishing the name, address and scan RSSI.</summary>
    public string Label => $"{Name} — {Address:X12} — {Rssi} dBm";
}

/// <summary>Fusionne annonce et scan response par adresse, sans filtre qui perdrait le nom.</summary>
/// <param name="pendingName">Supplies a localized placeholder when the name has not yet been received.</param>
public sealed class AdvertisementIndex(Func<string>? pendingName = null)
{
    /// <summary>The firmware's UART BLE service identifier.</summary>
    public static readonly Guid ServiceId = new("6e400001-b5a3-f393-e0a9-e50e24dcca9e");

    private readonly Dictionary<ulong, (string Name, bool Service)> _seen = [];

    /// <summary>Merges an advertisement with earlier data for its address.</summary>
    /// <returns>A matching controller candidate, or null for an unrelated advertisement.</returns>
    public BleCandidate? Add(ulong address, string name, bool hasService, short rssi)
    {
        _seen.TryGetValue(address, out var previous);
        var mergedName = string.IsNullOrWhiteSpace(name) ? previous.Name ?? "" : name;
        var service = hasService || previous.Service;
        _seen[address] = (mergedName, service);
        return service || mergedName == "ESP32-FanController"
            ? new(address, string.IsNullOrEmpty(mergedName) ? pendingName?.Invoke() ?? "UART controller (pending name)" : mergedName, rssi)
            : null;
    }
}
