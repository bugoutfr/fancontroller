using System.Text.Json;
using FanController.Core;
using Xunit;

namespace FanController.Tests;

internal static class ControllerTestData
{
    internal static Configuration Config => new(1,
        [[new(25, 30), new(65, 100)], [new(20, 20), new(65, 100)]],
        [new("auto", 0), new("fixed", 45.5), new("auto", 0), new("fixed", 60)]);

    internal static Telemetry Status => new(uint.MaxValue, true, "loaded", true,
        [new(1, 1650.0625, 25.5, "none", false), new(2, 3300, null, "open_circuit", true)],
        Enumerable.Range(1, 4).Select(f => new FanStatus(f, (f - 1) / 2 + 1,
            "auto", 0, 50.25, 50.25, 2200.75, true, true)).ToArray());

    internal static void ReplyConfig(FakeTransport transport) => transport.Reply(JsonSerializer.Serialize(
        new
        {
            ok = true,
            cmd = "get_config",
            dirty = true,
            storage = "loaded",
            data = Config
        }, WireJson.Options));

    internal static void ReplyStatus(FakeTransport transport) => transport.Reply(JsonSerializer.Serialize(
        new
        {
            ok = true,
            cmd = "get_status",
            data = Status
        }, WireJson.Options));

    internal static async Task SynchronizeAsync(FakeTransport transport)
    {
        Assert.Equal("get_config", (await transport.NextAsync()).GetProperty("cmd").GetString());
        ReplyConfig(transport);
        Assert.Equal("get_status", (await transport.NextAsync()).GetProperty("cmd").GetString());
        ReplyStatus(transport);
    }
}
