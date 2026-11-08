namespace FanController.Core;

/// <summary>Monotonic timing and acknowledged bytes for the binary upload only.</summary>
/// <param name="AcknowledgedBytes">Bytes confirmed written by the controller.</param>
/// <param name="TotalBytes">Validated image size.</param>
/// <param name="Elapsed">Duration since the first binary block, excluding preparation and validation.</param>
public sealed record FirmwareTransferProgress(uint AcknowledgedBytes, int TotalBytes, TimeSpan Elapsed)
{
    /// <summary>Mean acknowledged upload throughput in KiB/s, zero before timed data exists.</summary>
    public double KibibytesPerSecond => Elapsed.TotalSeconds > 0
        ? AcknowledgedBytes / 1024.0 / Elapsed.TotalSeconds : 0;
}
