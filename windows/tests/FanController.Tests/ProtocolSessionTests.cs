using System.Text;
using FanController.Core;
using Xunit;

namespace FanController.Tests;

public sealed class ProtocolSessionTests
{
    [Fact]
    public async Task CommandsAreLfTerminatedAndWrittenInTwentyByteBlocks()
    {
        using var transport = new FakeTransport();
        using var session = new ProtocolSession(transport);
        var request = session.RequestAsync(new
        {
            cmd = "set_profile",
            sensor = 1,
            points = new[] { new CurvePoint(25.5, 30), new CurvePoint(60, 100) }
        });
        var command = await transport.NextAsync();
        Assert.Equal(25.5, command.GetProperty("points")[0].GetProperty("temperature").GetDouble());
        Assert.Equal(1, command.GetProperty("sensor").GetInt32());
        Assert.All(transport.Writes, w => Assert.InRange(w.Length, 1, 20));
        Assert.All(transport.Writes.SkipLast(1), w => Assert.Equal(20, w.Length));
        Assert.Equal((byte)10, transport.Writes[^1][^1]);
        transport.Reply("""{"ok":true,"cmd":"set_profile","dirty":true}""", 1);
        Assert.True((await request).GetProperty("dirty").GetBoolean());
    }
    [Fact]
    public async Task ConcurrentActionsNeverWriteUntilPreviousReplyIsComplete()
    {
        using var transport = new FakeTransport();
        using var session = new ProtocolSession(transport);
        var first = session.RequestAsync(new
        {
            cmd = "get_status"
        });
        await transport.NextAsync();
        var second = session.RequestAsync(new
        {
            cmd = "save"
        });
        Assert.False(transport.Commands.Reader.TryRead(out _));
        transport.Emit(Encoding.UTF8.GetBytes("""{"ok":true,"cmd":"get_status"}"""));
        Assert.False(first.IsCompleted);
        Assert.False(transport.Commands.Reader.TryRead(out _));
        transport.Emit([10]);
        await first;
        Assert.Equal("save", (await transport.NextAsync()).GetProperty("cmd").GetString());
        transport.Reply("""{"ok":true,"cmd":"save","dirty":false}""");
        await second;
    }
    [Fact]
    public async Task TimeoutInvalidatesSessionAndLateResponseCannotSatisfyQueuedRequest()
    {
        using var transport = new FakeTransport();
        using var session = new ProtocolSession(transport, TimeSpan.FromMilliseconds(100));
        var first = session.RequestAsync(new
        {
            cmd = "get_status"
        });
        await transport.NextAsync();
        var queued = session.RequestAsync(new
        {
            cmd = "save"
        });
        await Assert.ThrowsAsync<TimeoutException>(() => first);
        transport.Reply("""{"ok":true,"cmd":"get_status"}""");
        await Assert.ThrowsAsync<IOException>(() => queued);
        Assert.False(session.IsValid);
        Assert.Single(transport.Names);
        Assert.Equal(1, transport.DisconnectCount);
    }
    [Fact]
    public async Task WriteTimeoutAlsoClosesConnection()
    {
        using var transport = new FakeTransport { BlockWrite = true };
        using var session = new ProtocolSession(transport, TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<TimeoutException>(() => session.RequestAsync(new { cmd = "save" }));
        Assert.False(transport.Connected);
    }
    [Theory]
    [InlineData("""{"ok":false,"error":"storage_error","message":"Erreur mémoire"}""")]
    [InlineData("""{"ok":true,"cmd":"save"}""")]
    [InlineData("""{"ok":true}""")]
    [InlineData("pas du json")]
    [InlineData("[]")]
    [InlineData("{\"ok\":true,\"cmd\":\"get_status\"}\n{\"ok\":true,\"cmd\":\"get_status\"}")]
    [InlineData("{\"ok\":true,\"cmd\":\"get_status\"}\ntrailing")]
    public async Task ErrorsMalformedOrMismatchedResponsesInvalidate(string response)
    {
        using var transport = new FakeTransport();
        using var session = new ProtocolSession(transport);
        var request = session.RequestAsync(new
        {
            cmd = "get_status"
        });
        await transport.NextAsync();
        transport.Reply(response, 1000);
        await Assert.ThrowsAnyAsync<Exception>(() => request);
        Assert.False(session.IsValid);
        Assert.False(transport.Connected);
    }
    [Fact]
    public async Task ErrorWithoutCommandIsReportedToUser()
    {
        using var transport = new FakeTransport();
        using var session = new ProtocolSession(transport);
        var task = session.RequestAsync(new
        {
            cmd = "save"
        });
        await transport.NextAsync();
        transport.Reply("""{"ok":false,"error":"storage_error","message":"écriture refusée"}""");
        var error = await Assert.ThrowsAsync<IOException>(() => task);
        Assert.Contains("écriture refusée", error.Message);
    }
    [Fact]
    public async Task GattFailureAndLinkLossCloseTheSession()
    {
        using var broken = new FakeTransport { ThrowOnWrite = true };
        using var s1 = new ProtocolSession(broken);
        await Assert.ThrowsAsync<IOException>(() => s1.RequestAsync(new { cmd = "save" }));
        Assert.False(s1.IsValid);
        using var transport = new FakeTransport();
        using var s2 = new ProtocolSession(transport);
        var task = s2.RequestAsync(new
        {
            cmd = "save"
        });
        await transport.NextAsync();
        transport.LoseConnection();
        await Assert.ThrowsAsync<IOException>(() => task);
        Assert.False(s2.IsValid);
    }
    [Fact]
    public async Task CancellationWhileWaitingDoesNotInvalidateButInFlightCancellationDoes()
    {
        using var transport = new FakeTransport();
        using var session = new ProtocolSession(transport);
        using var inFlight = new CancellationTokenSource();
        using var queued = new CancellationTokenSource();
        var first = session.RequestAsync(new
        {
            cmd = "get_status"
        }, inFlight.Token);
        await transport.NextAsync();
        var second = session.RequestAsync(new
        {
            cmd = "save"
        }, queued.Token);
        queued.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.True(session.IsValid);
        inFlight.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(session.IsValid);
    }
    [Fact]
    public async Task OversizeCommandNeverWritesAndSessionRemainsUsable()
    {
        using var transport = new FakeTransport();
        using var session = new ProtocolSession(transport);
        await Assert.ThrowsAsync<ArgumentException>(() => session.RequestAsync(new { cmd = new string('x', 1024) }));
        Assert.Empty(transport.Writes);
        Assert.True(session.IsValid);
    }
    [Fact]
    public void UnsolicitedNotificationInvalidates()
    {
        using var transport = new FakeTransport();
        using var session = new ProtocolSession(transport);
        transport.Reply("""{"ok":true,"cmd":"save"}""");
        Assert.False(session.IsValid);
    }
}
