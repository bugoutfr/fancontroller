using FanController.App.ViewModels;
using FanController.Core;
using Xunit;

namespace FanController.Tests;

public sealed class HistoryViewModelTests
{
    [Fact]
    public async Task RangeChangeDuringReadIsReloadedBeforePublishingTheResult()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var durations = new List<TimeSpan>();
        var store = new TestHistoryStore
        {
            OnRead = async (_, from, until, ct) =>
            {
                durations.Add(until - from);
                if (durations.Count == 1)
                {
                    entered.SetResult();
                    await release.Task.WaitAsync(ct);
                }
                return [HistorySample.FromTelemetry(until, ControllerTestData.Status)];
            }
        };
        var errors = new List<Exception>();
        await using var model = new HistoryViewModel(new InlineDispatcher(), store, errors.Add);
        model.SelectDevice(42);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        model.RangeIndex = 2;
        release.SetResult();
        await model.RefreshCommand.ExecutionTask;

        Assert.Equal(new[] { TimeSpan.FromHours(1), TimeSpan.FromDays(7) }, durations);
        Assert.Equal(2, model.Temperatures.Count);
        Assert.Single(model.Temperatures[0].Points);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task ShutdownCancelsHistoryReadBeforeDisposingStore()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;
        var store = new TestHistoryStore
        {
            OnRead = async (_, _, _, ct) =>
            {
                entered.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                finally { finished = true; }
                return [];
            }
        };
        var errors = new List<Exception>();
        var model = new HistoryViewModel(new InlineDispatcher(), store, errors.Add);
        model.SelectDevice(42);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await model.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(finished);
        Assert.True(store.Disposed);
        Assert.False(model.RefreshCommand.CanExecute(null));
        Assert.Empty(errors);
    }

    [Fact]
    public async Task CorruptHistoryIsReportedWithoutFabricatedMeasurements()
    {
        var errors = new List<Exception>();
        var store = new TestHistoryStore
        {
            OnRead = (_, _, _, _) => throw new InvalidDataException("History corrupt")
        };
        await using var model = new HistoryViewModel(new InlineDispatcher(), store, errors.Add);
        model.SelectDevice(42);
        await model.RefreshCommand.ExecutionTask;

        Assert.Contains("History corrupt", model.Message);
        Assert.Empty(model.Temperatures);
        Assert.Single(errors);
    }
}
