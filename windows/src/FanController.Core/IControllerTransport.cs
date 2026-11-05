namespace FanController.Core;

/// <summary>A BLE transport that distinguishes manual pairing from remembered-device reconnection.</summary>
public interface IControllerTransport : IBleTransport
{
    /// <summary>Gets the name read from the connected device, if available.</summary>
    string? DeviceName
    {
        get;
    }

    /// <summary>Connects and subscribes to notifications before any protocol request.</summary>
    /// <param name="address">The 48-bit Bluetooth address.</param>
    /// <param name="cancellationToken">Cancels discovery or pairing.</param>
    /// <param name="allowPairing">Allows pairing only for an explicit user connection.</param>
    Task ConnectAsync(ulong address, CancellationToken cancellationToken, bool allowPairing);
}
