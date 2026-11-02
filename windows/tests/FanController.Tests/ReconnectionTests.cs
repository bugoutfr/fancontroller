using FanController.Core;
using Xunit;

namespace FanController.Tests;

public sealed class ReconnectionTests
{
    [Fact]
    public void RemembersOnlyExplicitlySavedAddressAcrossInstances()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FanController-connection-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "last-device.txt");
        try
        {
            var store = new ConnectionPreferences(path);
            Assert.Null(store.Load());
            store.Save(0x123456789ABC);
            Assert.Equal(0x123456789ABCul, new ConnectionPreferences(path).Load());
            store.Save(42);
            Assert.Equal(42ul, store.Load());
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Throws<ArgumentOutOfRangeException>(() => store.Save(0x1000000000000));
            File.WriteAllText(path, "invalid");
            Assert.Throws<InvalidDataException>(() => store.Load());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task RepeatsFailuresIndefinitelyWithThirtySecondCapAndReportsErrors()
    {
        using var lifetime = new CancellationTokenSource();
        var waits = new List<double>();
        var errors = new List<Exception>();
        var attempts = 0;
        var loop = new AutoReconnectLoop(new(), () => 42, (address, ct) =>
        {
            Assert.Equal(42ul, address);
            attempts++;
            throw new IOException("Hors de portée");
        }, errors.Add, (delay, ct) =>
        {
            waits.Add(delay.TotalSeconds);
            if (waits.Count == 10) lifetime.Cancel();
            return Task.CompletedTask;
        });
        await loop.RunAsync(lifetime.Token);
        Assert.Equal(10, attempts);
        Assert.Equal(10, errors.Count);
        Assert.Equal(new double[] { 4, 8, 16, 30, 30, 30, 30, 30, 30, 30 }, waits);
    }

    [Fact]
    public async Task ConnectsAtStartupThenReconnectsAfterLossButNotAfterExplicitDisconnect()
    {
        using var lifetime = new CancellationTokenSource();
        var policy = new ReconnectPolicy();
        var connected = false;
        var attempts = 0;
        var ticks = 0;
        var loop = new AutoReconnectLoop(policy, () => connected ? null : 42, (address, ct) =>
        {
            attempts++;
            connected = true;
            return Task.FromResult(true);
        }, ex => Assert.Fail(ex.Message), (delay, ct) =>
        {
            ticks++;
            if (ticks == 1) connected = false; // Unexpected link loss.
            if (ticks == 2) { policy.Pause(); connected = false; }
            if (ticks == 4) policy.Resume(); // Explicit Connect.
            if (ticks == 5) lifetime.Cancel();
            return Task.CompletedTask;
        });
        await loop.RunAsync(lifetime.Token);
        Assert.Equal(3, attempts);
        Assert.Equal(TimeSpan.FromSeconds(2), policy.Delay);
        Assert.False(new ReconnectPolicy().Paused); // New app lifetime resumes attempts.
    }

    [Fact]
    public async Task DoesNotChooseAnotherDeviceWhenNoneIsRememberedOrEligible()
    {
        using var lifetime = new CancellationTokenSource();
        var attempts = 0;
        var loop = new AutoReconnectLoop(new(), () => null, (_, _) =>
        {
            attempts++;
            return Task.FromResult(true);
        }, ex => Assert.Fail(ex.Message), (_, _) =>
        {
            lifetime.Cancel();
            return Task.CompletedTask;
        });
        await loop.RunAsync(lifetime.Token);
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task ShutdownCancelsInFlightConnectionWithoutRetryOrSpuriousError()
    {
        using var lifetime = new CancellationTokenSource();
        var attempts = 0;
        var errors = 0;
        var loop = new AutoReconnectLoop(new(), () => 42, async (_, ct) =>
        {
            attempts++;
            lifetime.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return true;
        }, _ => errors++);
        await loop.RunAsync(lifetime.Token);
        Assert.Equal(1, attempts);
        Assert.Equal(0, errors);
    }

    [Fact]
    public async Task UnpairedDeviceFailuresBackOffWithoutAssumingSuccess()
    {
        using var lifetime = new CancellationTokenSource();
        var policy = new ReconnectPolicy();
        var attempts = 0;
        var loop = new AutoReconnectLoop(policy, () => 42, (_, _) =>
        {
            attempts++;
            return Task.FromResult(false);
        }, ex => Assert.Fail(ex.Message), (_, _) =>
        {
            if (attempts == 3) lifetime.Cancel();
            return Task.CompletedTask;
        });
        await loop.RunAsync(lifetime.Token);
        Assert.Equal(3, attempts);
        Assert.Equal(TimeSpan.FromSeconds(16), policy.Delay);
    }
}
