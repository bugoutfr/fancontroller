using System.Text.Json;
using FanController.Core;
using Xunit;

namespace FanController.Tests;

public sealed class HistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "FanController-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static HistorySample Sample(DateTimeOffset time, double rpm = 1200, string session = "session-a") =>
        new(time, 1000, [22.9, null], [rpm, 1300, 1400, 1500], session);

    [Fact]
    public async Task PrunesOnStartupEvenWithoutANewBluetoothSample()
    {
        var store = new HistoryStore(_directory);
        await store.AppendAsync(1, Sample(Now.AddDays(-31)));
        var reopened = new HistoryStore(_directory);
        await reopened.PruneAsync(Now);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task DetectsGapsBeforeDownsamplingRatherThanBreakingNormalLongRangeCurves()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"000000000001-{Now:yyyy-MM-dd}.jsonl");
        using (var writer = File.CreateText(path))
            for (var i = 0; i < 1000; i++)
                await writer.WriteLineAsync(JsonSerializer.Serialize(Sample(Now.AddSeconds(i * 2 + (i >= 500 ? 120 : 0))),
                    WireJson.Options));
        var read = await new HistoryStore(_directory).ReadAsync(1, Now.AddDays(-29), Now.AddHours(1));
        Assert.Equal(new[] { 0, 1 }, read.Select(sample => sample.Segment).Distinct());
        Assert.Contains(read.Zip(read.Skip(1)), pair => pair.First.Segment == pair.Second.Segment &&
            pair.Second.Timestamp - pair.First.Timestamp > TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task PersistsAcrossInstancesSeparatesDevicesAndPreservesMissingSensorAndSession()
    {
        var store = new HistoryStore(_directory);
        await store.AppendAsync(0x1234, Sample(Now));
        await store.AppendAsync(0x9876, Sample(Now, 900));
        var reopened = new HistoryStore(_directory);
        var read = await reopened.ReadAsync(0x1234, Now.AddHours(-1), Now.AddMinutes(1));
        var sample = Assert.Single(read);
        Assert.Equal(22.9, sample.Temperatures[0]);
        Assert.Null(sample.Temperatures[1]);
        Assert.Equal(1200, sample.Rpm[0]);
        Assert.Equal("session-a", sample.SessionId);
        Assert.Equal(new ulong[] { 0x1234, 0x9876 }, reopened.Devices());
    }

    [Fact]
    public async Task RemovesOldDailyFilesAndFiltersRetentionBoundary()
    {
        var store = new HistoryStore(_directory);
        await store.AppendAsync(1, Sample(Now.AddDays(-31)));
        await store.AppendAsync(1, Sample(Now.AddDays(-30).AddHours(-1)));
        await store.AppendAsync(1, Sample(Now.AddDays(-29)));
        await store.AppendAsync(1, Sample(Now));
        Assert.False(File.Exists(Path.Combine(_directory, $"000000000001-{Now.AddDays(-31):yyyy-MM-dd}.jsonl")));
        var read = await store.ReadAsync(1, Now.AddDays(-90), Now);
        Assert.Equal(2, read.Count);
        Assert.All(read, sample => Assert.True(sample.Timestamp >= Now.AddDays(-30)));
    }

    [Fact]
    public async Task StreamingEnvelopeKeepsSpikesAndIsBounded()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"000000000001-{Now:yyyy-MM-dd}.jsonl");
        using (var writer = File.CreateText(path))
            for (var i = 0; i < 20000; i++)
                await writer.WriteLineAsync(JsonSerializer.Serialize(Sample(Now.AddMilliseconds(i), i == 10001 ? 9999 : 1000),
                    WireJson.Options));
        var read = await new HistoryStore(_directory).ReadAsync(1, Now, Now.AddDays(1));
        Assert.True(read.Count < 100);
        Assert.Contains(read, sample => sample.Rpm[0] == 9999);
        Assert.Equal(read.OrderBy(sample => sample.Timestamp), read);
    }

    [Fact]
    public async Task ConcurrentAppendsProduceWholeLinesAndCorruptionIsReported()
    {
        var store = new HistoryStore(_directory);
        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => store.AppendAsync(1, Sample(Now.AddSeconds(i)))));
        Assert.Equal(40, (await store.ReadAsync(1, Now, Now.AddHours(1))).Count);
        await File.AppendAllTextAsync(Directory.GetFiles(_directory).Single(), "{partial");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(1, Now, Now.AddHours(1)));
        Assert.Contains("ligne 41", error.Message);
    }

    [Fact]
    public async Task InvalidSamplesAreNotWritten()
    {
        var sample = Sample(Now) with { Rpm = [double.NaN, 0, 0, 0] };
        await Assert.ThrowsAsync<InvalidDataException>(() => new HistoryStore(_directory).AppendAsync(1, sample));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void FirmwareVersionIsOptionalForOldProtocolAndReadForNewFirmware()
    {
        var old = new Telemetry(1, false, "loaded", true,
            [new(1, 1000, 25, "none", false), new(2, 1000, 25, "none", false)],
            Enumerable.Range(1, 4).Select(f => new FanStatus(f, (f - 1) / 2 + 1, "auto", 0, 40, 40, 1000, true, false)).ToArray());
        var json = JsonSerializer.SerializeToElement(old, WireJson.Options);
        Assert.Null(WireJson.Read<Telemetry>(json).FirmwareVersion);
        json = JsonSerializer.SerializeToElement(old with { FirmwareVersion = "1.1.0" }, WireJson.Options);
        Assert.Equal("1.1.0", WireJson.Read<Telemetry>(json).FirmwareVersion);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
