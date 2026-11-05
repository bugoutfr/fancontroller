namespace FanController.Core;

/// <summary>Stores and reads local controller measurements independently of the UI.</summary>
public interface IHistoryStore
{
    /// <summary>Lists controller addresses with stored measurements.</summary>
    IReadOnlyList<ulong> Devices();

    /// <summary>Appends a validated sample to the controller's daily file.</summary>
    Task AppendAsync(ulong device, HistorySample sample, CancellationToken ct = default);

    /// <summary>Removes daily files outside the retention period.</summary>
    Task PruneAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Reads a period, preserving extrema and gaps while bounding the result size.</summary>
    /// <exception cref="InvalidDataException">A stored sample is corrupt.</exception>
    Task<IReadOnlyList<HistorySample>> ReadAsync(
        ulong device, DateTimeOffset from, DateTimeOffset until, CancellationToken ct = default);
}
