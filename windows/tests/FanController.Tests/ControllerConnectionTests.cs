using FanController.Core;
using Xunit;

namespace FanController.Tests;

public sealed class ControllerConnectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "FanController-lifetime-" + Guid.NewGuid().ToString("N"));
    private ConnectionPreferences Preferences => new(Path.Combine(_directory, "last-device.txt"));

    [Fact]
    public async Task ManualConnectionReadsOnlyAndDisposesAfterPollingIsCancelled()
    {
        var transport = new FakeTransport();
        var history = new TestHistoryStore();
        var updates = new List<ConnectionUpdate>();
        await using var connection = new ControllerConnection(
            () => transport, (_, _) => Task.FromResult(true), Preferences, history);
        connection.Changed += updates.Add;

        var connecting = connection.ConnectAsync(42);
        await ControllerTestData.SynchronizeAsync(transport);
        await connecting;
        Assert.True(transport.PairingAllowed);
        Assert.Equal(42ul, Preferences.Load());
        Assert.Equal(new[] { "get_config", "get_status" }, transport.Names);
        Assert.Single(history.Samples);
        Assert.NotNull(history.Samples[0].Sample.SessionId);
        Assert.Equal(ConnectionStage.Connected, updates[^1].Stage);

        await connection.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(transport.Disposed);
        Assert.True(connection.ReconnectionPaused);
        Assert.Equal(ConnectionStage.Disconnected, updates[^1].Stage);
        await connection.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task DisconnectCancelsInitialRequestAndWaitsForItsCleanup()
    {
        var transport = new FakeTransport();
        await using var connection = new ControllerConnection(
            () => transport, (_, _) => Task.FromResult(true), Preferences, new TestHistoryStore());
        var connecting = connection.ConnectAsync(42);
        await transport.NextAsync();

        await connection.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        Assert.True(transport.Disposed);
        Assert.Null(Preferences.Load());
        Assert.Single(transport.Names);
    }

    [Fact]
    public async Task DisconnectCancelsAnInFlightPollingReadBeforeReleasingTransport()
    {
        var transport = new FakeTransport();
        var history = new TestHistoryStore();
        await using var connection = new ControllerConnection(
            () => transport, (_, _) => Task.FromResult(true), Preferences, history);
        var connecting = connection.ConnectAsync(42);
        await ControllerTestData.SynchronizeAsync(transport);
        await connecting;
        Assert.Equal("get_status", (await transport.NextAsync()).GetProperty("cmd").GetString());

        await connection.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(transport.Disposed);
        Assert.Single(history.Samples);
        Assert.Equal(1, transport.DisconnectCount);
    }

    [Fact]
    public async Task ShutdownCancelsQueuedRequestsAsWellAsTheActiveRequest()
    {
        var transport = new FakeTransport();
        var connection = new ControllerConnection(
            () => transport, (_, _) => Task.FromResult(true), Preferences, new TestHistoryStore());
        var connecting = connection.ConnectAsync(42);
        await transport.NextAsync();
        var read = connection.SynchronizeAsync();
        var save = connection.SaveAsync();

        await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
        Assert.Equal(new[] { "get_config" }, transport.Names);
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task ShutdownCancelsInFlightDiscoveryWithoutRetrying()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport
        {
            OnConnect = async ct =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        };
        var connection = new ControllerConnection(
            () => transport, (_, _) => Task.FromResult(true), Preferences, new TestHistoryStore());
        var connecting = connection.ConnectAsync(42);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        Assert.True(transport.Disposed);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task ShutdownWaitsForAnInFlightHistoryWriteBeforeDisposingSession()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport();
        var history = new TestHistoryStore
        {
            BeforeAppend = async ct =>
            {
                entered.SetResult();
                await released.Task;
                ct.ThrowIfCancellationRequested();
            }
        };
        var connection = new ControllerConnection(
            () => transport, (_, _) => Task.FromResult(true), Preferences, history);
        var connecting = connection.ConnectAsync(42);
        await ControllerTestData.SynchronizeAsync(transport);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var shutdown = connection.DisposeAsync().AsTask();
        Assert.False(shutdown.IsCompleted);
        Assert.False(transport.Disposed);
        released.SetResult();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task DiskFailureIsReportedWithoutLosingAHealthyConnection()
    {
        var transport = new FakeTransport();
        var history = new TestHistoryStore { BeforeAppend = _ => throw new IOException("Disk full") };
        var errors = new List<Exception>();
        await using var connection = new ControllerConnection(
            () => transport, (_, _) => Task.FromResult(true), Preferences, history);
        connection.HistoryWriteFailed += errors.Add;
        var connecting = connection.ConnectAsync(42);
        await ControllerTestData.SynchronizeAsync(transport);
        await connecting;

        Assert.Equal("Disk full", Assert.Single(errors).Message);
        Assert.True(transport.Connected);
        Assert.False(transport.Disposed);
    }

    [Fact]
    public async Task AutomaticConnectionNeverPairsAndNeverReplaysAnInterruptedWrite()
    {
        Preferences.Save(42);
        var first = new FakeTransport();
        var second = new FakeTransport();
        var transports = new Queue<FakeTransport>([first, second]);
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connections = 0;
        await using var connection = new ControllerConnection(
            () => transports.Dequeue(), (_, _) => Task.FromResult(true), Preferences, new TestHistoryStore());
        connection.Changed += update =>
        {
            if (update.Stage == ConnectionStage.Connected && ++connections == 2)
                reconnected.TrySetResult();
        };
        connection.Start();
        await ControllerTestData.SynchronizeAsync(first);
        Assert.False(first.PairingAllowed);
        var apply = connection.ApplyAsync(ControllerTestData.Config);
        Assert.Equal("set_profile", (await first.NextAsync()).GetProperty("cmd").GetString());
        first.LoseConnection();
        await Assert.ThrowsAnyAsync<Exception>(() => apply);

        await ControllerTestData.SynchronizeAsync(second);
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(second.PairingAllowed);
        Assert.Equal(new[] { "get_config", "get_status" }, second.Names);
        Assert.True(first.Disposed);
    }

    [Fact]
    public async Task PauseCancelsPairingCheckAndDoesNotCreateATransport()
    {
        Preferences.Save(42);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;
        await using var connection = new ControllerConnection(
            () => { factoryCalls++; return new FakeTransport(); },
            async (_, ct) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return true;
            }, Preferences, new TestHistoryStore());
        connection.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await connection.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, factoryCalls);
        Assert.True(connection.ReconnectionPaused);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
