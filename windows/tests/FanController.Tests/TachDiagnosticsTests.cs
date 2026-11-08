using System.Globalization;
using System.Text.Json;
using FanController.Core;
using Xunit;

namespace FanController.Tests;

public sealed class TachDiagnosticsTests
{
    internal static TachWindow Window(ulong sequence) => new(sequence, (uint)sequence * 1000,
        Enumerable.Range(1, 4).Select(fan => new TachFanReading(fan, 1000000, 40, 40,
            25000, 25000, 1000000, 1200, 1200, 1200, 45.5)).ToArray());

    internal static void Reply(FakeTransport transport, ulong latest, TachWindow? window = null, bool dropped = false) =>
        transport.Reply(JsonSerializer.Serialize(new
        {
            ok = true,
            cmd = "get_tach_diagnostics",
            data = new TachReply(latest, dropped, window),
            dirty = false
        }, WireJson.Options));

    internal static async Task CompleteCapture(FakeTransport transport, int seconds)
    {
        var baseline = await transport.NextAsync();
        Assert.Equal("get_tach_diagnostics", baseline.GetProperty("cmd").GetString());
        Assert.False(baseline.TryGetProperty("after", out _));
        Reply(transport, 10);
        for (ulong sequence = 11; sequence <= (ulong)(10 + seconds); sequence++)
        {
            var request = await transport.NextAsync();
            Assert.Equal("get_tach_diagnostics", request.GetProperty("cmd").GetString());
            Assert.Equal(sequence - 1, request.GetProperty("after").GetUInt64());
            Reply(transport, sequence, Window(sequence));
        }
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public async Task CaptureReadsEveryWindowAndSerializesPolling(int seconds)
    {
        using var transport = new FakeTransport();
        using var session = new ProtocolSession(transport);
        using var service = new ControllerService(session);
        var capture = service.CaptureTachAsync(seconds, null);
        var polling = service.StatusAsync();
        await CompleteCapture(transport, seconds);
        var result = await capture.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(seconds, result.Count);
        Assert.Equal(11ul, result[0].Sequence);
        Assert.Equal((ulong)(10 + seconds), result[^1].Sequence);
        Assert.Equal("get_status", (await transport.NextAsync()).GetProperty("cmd").GetString());
        ControllerTestData.ReplyStatus(transport);
        await polling;
        Assert.True(service.IsConnected);
        Assert.DoesNotContain("set_fan", transport.Names);
    }

    [Theory]
    [InlineData(true, 12ul, 12ul)]
    [InlineData(false, 12ul, 12ul)]
    [InlineData(false, 1ul, 1ul)]
    public async Task CaptureRejectsDroppedSkippedOrRestartedWindows(bool dropped, ulong latest, ulong sequence)
    {
        using var transport = new FakeTransport();
        using var session = new ProtocolSession(transport);
        using var service = new ControllerService(session);
        var capture = service.CaptureTachAsync(30, null);
        await transport.NextAsync();
        Reply(transport, 10);
        await transport.NextAsync();
        Reply(transport, latest, Window(sequence), dropped);
        await Assert.ThrowsAsync<InvalidDataException>(() => capture);
        Assert.False(service.IsConnected);
    }

    [Fact]
    public async Task NoNewWindowIsRetriedWithoutDuplicatingData()
    {
        using var transport = new FakeTransport();
        using var session = new ProtocolSession(transport);
        using var service = new ControllerService(session);
        var capture = service.CaptureTachAsync(30, null);
        await transport.NextAsync();
        Reply(transport, 10);
        await transport.NextAsync();
        Reply(transport, 10);
        for (ulong sequence = 11; sequence <= 40; sequence++)
        {
            Assert.Equal(sequence - 1, (await transport.NextAsync()).GetProperty("after").GetUInt64());
            Reply(transport, sequence, Window(sequence));
        }
        Assert.Equal(30, (await capture).Count);
    }

    [Fact]
    public async Task CancellationDoesNotReturnAPartialCapture()
    {
        using var transport = new FakeTransport();
        using var session = new ProtocolSession(transport);
        using var service = new ControllerService(session);
        using var cancellation = new CancellationTokenSource();
        var capture = service.CaptureTachAsync(30, null, cancellation.Token);
        await transport.NextAsync();
        Reply(transport, 0);
        await transport.NextAsync();
        Reply(transport, 1, Window(1));
        await transport.NextAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        Assert.False(service.IsConnected);
    }

    [Fact]
    public void CsvIsInvariantAndPreservesMissingPeriodsAndAllFans()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var window = Window(1);
            window.Fans[0] = window.Fans[0] with
            {
                Pulses = 0,
                Intervals = 0,
                MinUs = 0,
                MaxUs = 0,
                SpanUs = 0,
                RpmCount = 0,
                RpmPeriod = null,
                RpmDisplay = 0
            };
            TachDiagnostics.Validate(new(1, false, window), 0);
            var csv = TachDiagnostics.ToCsv([window]);
            Assert.Equal(6, csv.Split("\r\n").Length);
            Assert.Contains("1,1000,1,1000000,0,0,0,0,0,0.00,NA,0.00,45.50,NA,NA,NA,NA,NA,NA,NA,NA\r\n", csv);
            Assert.Contains("1,1000,4,1000000,40,40,25000,25000,1000000,1200.00,1200.00,1200.00,45.50,NA,NA,NA,NA,NA,NA,NA,NA\r\n", csv);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void InvalidMeasurementsAreRejected()
    {
        var window = Window(1);
        window.Fans[0] = window.Fans[0] with
        {
            RpmPeriod = double.NaN
        };
        Assert.Throws<InvalidDataException>(() => TachDiagnostics.Validate(new(1, false, window), 0));
        Assert.Throws<InvalidDataException>(() => TachDiagnostics.Validate(new(1, false, null), 0));
    }

    [Fact]
    public void TraceIsReadFromBleAndExportedInChronologicalOrder()
    {
        var window = Window(1);
        window.Fans[0] = window.Fans[0] with
        {
            TraceUs = [25000, 25000, 25000, 25000, 12500, 12500, 25000, 25000, 25000],
            TraceTriggerUs = 0,
            TraceReferenceUs = 25000
        };
        var json = JsonSerializer.Serialize(new TachReply(1, false, window), WireJson.Options);
        using var document = JsonDocument.Parse(json);
        var reply = WireJson.Read<TachReply>(document.RootElement);
        TachDiagnostics.Validate(reply, 0);
        Assert.Contains(",0,25000,25000;25000;25000;25000;12500;12500;25000;25000;25000,NA,NA,NA,NA,NA\r\n",
            TachDiagnostics.ToCsv([reply.Window!]));
        Assert.All(TachDiagnostics.ToCsv([window]).Split("\r\n").Where(line => line.Length != 0),
            line => Assert.Equal(21, line.Split(',').Length));
    }

    [Fact]
    public void PartialOrNonTriggeringTracesAreRejected()
    {
        var window = Window(1);
        window.Fans[0] = window.Fans[0] with
        {
            TraceTriggerUs = 1
        };
        Assert.Throws<InvalidDataException>(() => TachDiagnostics.Validate(new(1, false, window), 0));
        window.Fans[0] = window.Fans[0] with
        {
            TraceUs = [25000, 25000, 25000, 25000, 25000, 25000, 25000, 25000, 25000],
            TraceReferenceUs = 25000
        };
        Assert.Throws<InvalidDataException>(() => TachDiagnostics.Validate(new(1, false, window), 0));
    }

    [Fact]
    public void CorrectedRpmAndRawRpmRemainDistinctInExport()
    {
        var window = Window(1);
        window.Fans[0] = window.Fans[0] with
        {
            RpmDisplay = 1100,
            RpmCorrected = 1100,
            RpmRawDisplay = 1200,
            CorrectedIntervals = 36,
            CorrectedSpanUs = 981818,
            MergedPairs = 2
        };
        TachDiagnostics.Validate(new(1, false, window), 0);
        Assert.Contains(",1200.00,1100.00,36,981818,2\r\n", TachDiagnostics.ToCsv([window]));
        window.Fans[0] = window.Fans[0] with
        {
            CorrectedSpanUs = null
        };
        Assert.Throws<InvalidDataException>(() => TachDiagnostics.Validate(new(1, false, window), 0));
    }

    [Fact]
    public void OverwrittenHistoryAndRestartHaveDifferentErrors()
    {
        var overwritten = Assert.Throws<InvalidDataException>(() =>
            TachDiagnostics.Validate(new(30, true, null), 14));
        Assert.Contains("history overwritten", overwritten.Message);
        Assert.Contains("cursor 14, latest 30", overwritten.Message);
        var restarted = Assert.Throws<InvalidDataException>(() =>
            TachDiagnostics.Validate(new(1, true, null), 14));
        Assert.Contains("sequence restarted", restarted.Message);
    }

    [Fact]
    public async Task CaptureCanDrainSixtyRetainedWindowsWhileFirmwareIsAhead()
    {
        using var transport = new FakeTransport();
        using var session = new ProtocolSession(transport);
        using var service = new ControllerService(session);
        var capture = service.CaptureTachAsync(60, null);
        await transport.NextAsync();
        Reply(transport, 80);
        for (ulong sequence = 81; sequence <= 140; ++sequence)
        {
            Assert.Equal(sequence - 1, (await transport.NextAsync()).GetProperty("after").GetUInt64());
            Reply(transport, 140, Window(sequence));
        }
        var windows = await capture;
        Assert.Equal(60, windows.Count);
        Assert.Equal(140ul, windows[^1].Sequence);
    }
}
