namespace FanController.Core;

/// <summary>Optional binary OTA channel on an encrypted controller connection.</summary>
public interface IFirmwareTransport
{
    /// <summary>Whether the peer exposes the OTA characteristic.</summary>
    bool FirmwareUpdateSupported
    {
        get;
    }

    /// <summary>Maximum image bytes per packet, excluding its four-byte offset.</summary>
    int FirmwareBlockSize
    {
        get;
    }

    /// <summary>Writes one block and awaits the firmware's flash-write acknowledgment. Never retry an ambiguous write.</summary>
    /// <param name="offset">Absolute image offset.</param>
    /// <param name="bytes">Image bytes, within the negotiated block size.</param>
    /// <param name="ct">Cancellation closes the connection if a write may have started.</param>
    /// <returns>Total bytes acknowledged by the firmware.</returns>
    Task<uint> WriteFirmwareBlockAsync(uint offset, ReadOnlyMemory<byte> bytes, CancellationToken ct);
}
