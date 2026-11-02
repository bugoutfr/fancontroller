using System.Text.Json;
using FanController.Core;
using Xunit;

namespace FanController.Tests;

public sealed class ControllerServiceTests
{
    private static Configuration Config => new(1,
        [[new(25, 30), new(65, 100)], [new(20, 20), new(65, 100)]],
        [new("auto", 0), new("fixed", 45.5), new("auto", 0), new("fixed", 60)]);
    private static Telemetry Status => new(uint.MaxValue, true, "loaded", true,
        [new(1, 1650.0625, 25.5, "none", false), new(2, 3300, null, "open_circuit", true)],
        Enumerable.Range(1, 4).Select(f => new FanStatus(f, (f - 1) / 2 + 1,
            "auto", 0, 50.25, 50.25, 2200.75, true, true)).ToArray());
    private static void ReplyConfig(FakeTransport transport) => transport.Reply(JsonSerializer.Serialize(
        new { ok = true, cmd = "get_config", dirty = true, storage = "loaded", data = Config }, WireJson.Options));
    private static void ReplyStatus(FakeTransport transport) => transport.Reply(JsonSerializer.Serialize(
        new { ok = true, cmd = "get_status", data = Status }, WireJson.Options));
    private static void Ack(FakeTransport transport, string name) =>
        transport.Reply(JsonSerializer.Serialize(new { ok = true, cmd = name, dirty = true }));

    [Fact]
    public async Task ResynchronizationOnlyReadsAndHandlesNullTemperatureAndUintUptime()
    {
        using var transport = new FakeTransport();
        using var session = new ProtocolSession(transport);
        var service = new ControllerService(session);
        var task = service.SynchronizeAsync();
        Assert.Equal("get_config", (await transport.NextAsync()).GetProperty("cmd").GetString());
        ReplyConfig(transport);
        Assert.Equal("get_status", (await transport.NextAsync()).GetProperty("cmd").GetString());
        ReplyStatus(transport);
        var result = await task;
        Assert.Null(result.Status.Sensors[1].Temperature);
        Assert.Equal(uint.MaxValue, result.Status.UptimeMs);
        Assert.True(result.Config.Dirty);
        Assert.Equal(45.5, result.Config.Configuration.Fans[1].Duty);
        Assert.Equal(1650.0625, result.Status.Sensors[0].Millivolts);
        Assert.Equal(2200.75, result.Status.Fans[0].Rpm);
        Assert.Equal(50.25, result.Status.Fans[0].AppliedDuty);
        Assert.Equal(new[] { "get_config", "get_status" }, transport.Names);
    }

    [Fact]
    public async Task ApplyAndPollingAreSerializedAsWholeOperationsAndApplyNeverSaves()
    {
        using var transport = new FakeTransport();
        using var session = new ProtocolSession(transport);
        var service = new ControllerService(session);
        var apply = service.ApplyAsync(Config);
        var first = await transport.NextAsync();
        var poll = service.StatusAsync();
        Assert.False(transport.Commands.Reader.TryRead(out _));
        Assert.Equal(1, first.GetProperty("sensor").GetInt32());
        Ack(transport, "set_profile");
        var second = await transport.NextAsync();
        Assert.Equal(2, second.GetProperty("sensor").GetInt32());
        Ack(transport, "set_profile");
        for (var i = 0; i < 4; i++)
        {
            var fan = await transport.NextAsync();
            Assert.Equal("set_fan", fan.GetProperty("cmd").GetString());
            Assert.Equal(i + 1, fan.GetProperty("fan").GetInt32());
            Assert.Equal(Config.Fans[i].Mode, fan.GetProperty("mode").GetString());
            Assert.Equal(Config.Fans[i].Mode == "fixed", fan.TryGetProperty("duty", out _));
            Ack(transport, "set_fan");
        }
        Assert.Equal("get_config", (await transport.NextAsync()).GetProperty("cmd").GetString());
        ReplyConfig(transport);
        Assert.Equal("get_status", (await transport.NextAsync()).GetProperty("cmd").GetString());
        ReplyStatus(transport);
        await apply;
        Assert.Equal("get_status", (await transport.NextAsync()).GetProperty("cmd").GetString());
        ReplyStatus(transport);
        await poll;
        Assert.Equal(new[] { "set_profile", "set_profile", "set_fan", "set_fan", "set_fan", "set_fan",
            "get_config", "get_status", "get_status" }, transport.Names);
        Assert.DoesNotContain("save", transport.Names);
    }
    [Theory]
    [InlineData(false, "save")]
    [InlineData(true, "defaults")]
    public async Task SaveAndDefaultsAreDistinctCommandsFollowedByReadback(bool defaults, string name)
    {
        using var transport = new FakeTransport();
        using var session = new ProtocolSession(transport);
        var service = new ControllerService(session);
        var task = defaults ? service.DefaultsAsync() : service.SaveAsync();
        Assert.Equal(name, (await transport.NextAsync()).GetProperty("cmd").GetString());
        Ack(transport, name);
        Assert.Equal("get_config", (await transport.NextAsync()).GetProperty("cmd").GetString());
        ReplyConfig(transport);
        Assert.Equal("get_status", (await transport.NextAsync()).GetProperty("cmd").GetString());
        ReplyStatus(transport);
        await task;
        Assert.Equal(new[] { name, "get_config", "get_status" }, transport.Names);
    }
    [Fact]
    public async Task PartialApplyStopsOnErrorAndCannotContinueWithoutNewSession()
    {
        using var transport = new FakeTransport();
        using var session = new ProtocolSession(transport);
        var service = new ControllerService(session);
        var apply = service.ApplyAsync(Config);
        await transport.NextAsync();
        Ack(transport, "set_profile");
        await transport.NextAsync();
        transport.Reply("""{"ok":false,"error":"invalid_profile","message":"Rejected"}""");
        await Assert.ThrowsAsync<IOException>(() => apply);
        await Assert.ThrowsAsync<IOException>(() => service.StatusAsync());
        Assert.Equal(new[] { "set_profile", "set_profile" }, transport.Names);
        Assert.False(service.IsConnected);
    }
    [Fact]
    public async Task InvalidLocalDraftIsRejectedBeforeAnyWrite()
    {
        using var transport = new FakeTransport();
        using var session = new ProtocolSession(transport);
        var service = new ControllerService(session);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyAsync(Config with
        {
            Profiles = [[new(25, 30), new(20, 100)], [new(20, 20), new(65, 100)]]
        }));
        Assert.Empty(transport.Writes);
        Assert.True(session.IsValid);
    }
    [Theory]
    [InlineData("""{"ok":true,"cmd":"get_status","data":{}}""")]
    [InlineData("""{"ok":true,"cmd":"get_status","data":{"uptime_ms":1,"dirty":false,"storage":"loaded","storage_available":true,"sensors":[],"fans":[]}}""")]
    public async Task MissingTelemetryDoesNotBecomeFabricatedZeroValues(string response)
    {
        using var transport = new FakeTransport();
        using var session = new ProtocolSession(transport);
        var task = new ControllerService(session).StatusAsync();
        await transport.NextAsync();
        transport.Reply(response);
        await Assert.ThrowsAnyAsync<Exception>(() => task);
        Assert.False(session.IsValid);
    }
    [Fact]
    public async Task InvalidRemoteProfileAlsoInvalidatesTheSession()
    {
        using var transport = new FakeTransport();
        using var session = new ProtocolSession(transport);
        var task = new ControllerService(session).SynchronizeAsync();
        await transport.NextAsync();
        var bad = Config with { Profiles = [[new(20, 80), new(20, 40)], [new(20, 20), new(65, 100)]] };
        transport.Reply(JsonSerializer.Serialize(new { ok = true, cmd = "get_config", dirty = true, storage = "loaded", data = bad }, WireJson.Options));
        await Assert.ThrowsAsync<ArgumentException>(() => task);
        Assert.False(session.IsValid);
    }
}
