using FanController.Core;

namespace FanController.Tests;

internal sealed class TestHistoryStore : IHistoryStore, IDisposable
{
    internal List<(ulong Address, HistorySample Sample)> Samples { get; } = [];
    internal Func<CancellationToken, Task>? BeforeAppend
    {
        get; set;
    }
    internal Func<ulong, DateTimeOffset, DateTimeOffset, CancellationToken,
        Task<IReadOnlyList<HistorySample>>>? OnRead
    {
        get; set;
    }
    internal bool Disposed
    {
        get; private set;
    }

    public IReadOnlyList<ulong> Devices() => Samples.Select(sample => sample.Address).Distinct().ToArray();

    public async Task AppendAsync(ulong device, HistorySample sample, CancellationToken ct = default)
    {
        if (BeforeAppend is not null)
            await BeforeAppend(ct);
        Samples.Add((device, sample));
    }

    public Task PruneAsync(DateTimeOffset now, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<HistorySample>> ReadAsync(
        ulong device, DateTimeOffset from, DateTimeOffset until, CancellationToken ct = default) =>
        OnRead?.Invoke(device, from, until, ct) ??
        Task.FromResult<IReadOnlyList<HistorySample>>(
            Samples.Where(s => s.Address == device && s.Sample.Timestamp >= from && s.Sample.Timestamp <= until)
                .Select(s => s.Sample).ToArray());

    public void Dispose() => Disposed = true;
}
