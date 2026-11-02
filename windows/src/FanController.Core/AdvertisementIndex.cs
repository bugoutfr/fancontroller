namespace FanController.Core;

public sealed record BleCandidate(ulong Address, string Name, short Rssi)
{
    public string Label => $"{Name} — {Address:X12} — {Rssi} dBm";
}

/// <summary>Fusionne annonce et scan response par adresse, sans filtre qui perdrait le nom.</summary>
public sealed class AdvertisementIndex
{
    public static readonly Guid ServiceId = new("6e400001-b5a3-f393-e0a9-e50e24dcca9e");
    private readonly Dictionary<ulong, (string Name, bool Service)> _seen = [];
    public BleCandidate? Add(ulong address, string name, bool hasService, short rssi)
    {
        _seen.TryGetValue(address, out var previous);
        var mergedName = string.IsNullOrWhiteSpace(name) ? previous.Name ?? "" : name;
        var service = hasService || previous.Service;
        _seen[address] = (mergedName, service);
        return service || mergedName == "ESP32-FanController"
            ? new(address, string.IsNullOrEmpty(mergedName) ? "Contrôleur UART (nom en attente)" : mergedName, rssi)
            : null;
    }
}
