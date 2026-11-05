using FanController.App.Services;
using FanController.App.ViewModels;
using FanController.Core;
using Xunit;

namespace FanController.Tests;

internal sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

public sealed class ViewModelTests : IDisposable
{
    private sealed class StartupSettings : IStartupSettings
    {
        internal bool Value
        {
            get; set;
        }
        internal bool RejectChanges
        {
            get; set;
        }
        public bool Enabled() => Value;
        public void SetEnabled(bool enabled)
        {
            if (RejectChanges)
                throw new IOException("Startup setting unavailable");
            Value = enabled;
        }
    }

    private sealed class Scanner : IBleScanner
    {
        internal bool Disposed
        {
            get; private set;
        }
        public event Action<BleCandidate>? Found;
        public event Action<string>? Failed;
        public void Start() => Found?.Invoke(new(42, "Test controller", -50));
        public void Dispose() => Disposed = true;
        internal void Fail() => Failed?.Invoke("Scan failed");
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "FanController-viewmodels-" + Guid.NewGuid().ToString("N"));

    private MainViewModel Create(
        Func<IControllerTransport> transports, TestHistoryStore store,
        List<Exception> errors, StartupSettings? startup = null, Func<IBleScanner>? scanners = null)
    {
        var dispatcher = new InlineDispatcher();
        return new(
            new ControllerConnection(transports, (_, _) => Task.FromResult(true),
                new(Path.Combine(_directory, "last-device.txt")), store),
            new(dispatcher, store, errors.Add), dispatcher,
            startup ?? new(), scanners ?? (() => new Scanner()), errors.Add);
    }

    [Fact]
    public async Task ReconnectionPreservesDraftAndDiscardDoesNotWrite()
    {
        var first = new FakeTransport();
        var second = new FakeTransport();
        var transports = new Queue<FakeTransport>([first, second]);
        var store = new TestHistoryStore();
        var errors = new List<Exception>();
        await using var model = Create(() => transports.Dequeue(), store, errors);
        model.SelectedDevice = new(new(42, "Test", -50));
        var connecting = model.ConnectCommand.ExecuteAsync();
        await ControllerTestData.SynchronizeAsync(first);
        await connecting;
        Assert.True(model.Connected);
        Assert.True(model.CanEdit);
        Assert.Equal("Test controller", model.ControllerName);

        model.Profiles[0].Points[0].Duty = "35";
        Assert.True(model.ApplyCommand.CanExecute(null));
        await model.DisconnectCommand.ExecuteAsync();
        Assert.False(model.Connected);
        Assert.False(model.CanEdit);
        Assert.Equal("35", model.Profiles[0].Points[0].Duty);

        connecting = model.ConnectCommand.ExecuteAsync();
        await ControllerTestData.SynchronizeAsync(second);
        await connecting;
        Assert.True(model.Connected);
        Assert.Equal("35", model.Profiles[0].Points[0].Duty);
        Assert.True(model.ApplyCommand.CanExecute(null));
        Assert.False(model.SaveCommand.CanExecute(null));
        await model.AdoptCommand.ExecuteAsync();
        Assert.Equal("30", model.Profiles[0].Points[0].Duty);
        Assert.False(model.ApplyCommand.CanExecute(null));
        Assert.Equal(new[] { "get_config", "get_status" }, second.Names);
        Assert.Empty(errors);

        await model.DisposeAsync();
        Assert.True(store.Disposed);
        Assert.True(second.Disposed);
        Assert.False(model.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task ShutdownCancelsScanAndAwaitsTheCommand()
    {
        var scanner = new Scanner();
        var errors = new List<Exception>();
        var store = new TestHistoryStore();
        var model = Create(() => new FakeTransport(), store, errors, scanners: () => scanner);
        var scanning = model.ScanCommand.ExecuteAsync();
        Assert.Single(model.Devices);
        Assert.False(scanning.IsCompleted);

        await model.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(scanning.IsCompletedSuccessfully);
        Assert.True(scanner.Disposed);
        Assert.True(store.Disposed);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task ScannerConstructionFailureDoesNotLeaveScanningEnabled()
    {
        var errors = new List<Exception>();
        await using var model = Create(() => new FakeTransport(), new(), errors,
            scanners: () => throw new IOException("Scanner unavailable"));
        await model.ScanCommand.ExecuteAsync();

        Assert.Contains("Scanner unavailable", model.Error);
        Assert.Single(errors);
        Assert.True(model.ScanCommand.CanExecute(null));
    }

    [Fact]
    public async Task StartupFailuresAreReportedAndDoNotChangeTheDisplayedValue()
    {
        var errors = new List<Exception>();
        var settings = new StartupSettings { RejectChanges = true };
        await using var model = Create(() => new FakeTransport(), new(), errors, settings);
        model.LaunchAtStartup = true;

        Assert.False(model.LaunchAtStartup);
        Assert.False(settings.Value);
        Assert.Contains("Startup setting unavailable", model.Error);
        Assert.Single(errors);
    }

    [Fact]
    public async Task CommandsRejectOverlapAndExposeTheTaskIncludingErrorHandling()
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var errors = new List<Exception>();
        var command = new AsyncCommand(async () =>
        {
            calls++;
            await released.Task;
            throw new IOException("Command failed");
        }, () => true, errors.Add);
        var execution = command.ExecuteAsync();
        Assert.Same(execution, command.ExecutionTask);
        Assert.False(command.CanExecute(null));
        await command.ExecuteAsync();
        Assert.Equal(1, calls);
        released.SetResult();
        await execution;

        Assert.Equal("Command failed", Assert.Single(errors).Message);
        Assert.True(command.CanExecute(null));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
