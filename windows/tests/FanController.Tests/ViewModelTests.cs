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
    private static async Task ConnectTachModel(MainViewModel model, FakeTransport transport, bool supported)
    {
        model.SelectedDevice = new(new(42, "Test", -50));
        var connecting = model.ConnectCommand.ExecuteAsync();
        await transport.NextAsync();
        ControllerTestData.ReplyConfig(transport);
        await transport.NextAsync();
        transport.Reply(System.Text.Json.JsonSerializer.Serialize(new
        {
            ok = true,
            cmd = "get_status",
            data = ControllerTestData.Status with
            {
                TachDiagnosticsSupported = supported
            }
        }, WireJson.Options));
        await connecting;
    }

    [Fact]
    public async Task OldFirmwareDisablesCaptureWithoutSendingUnsupportedCommands()
    {
        var transport = new FakeTransport();
        var errors = new List<Exception>();
        await using var model = Create(() => transport, new TestHistoryStore(), errors);
        await ConnectTachModel(model, transport, false);
        Assert.False(model.TachSupported);
        Assert.False(model.CanCaptureTach);
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.CaptureTachAsync(30));
        Assert.Equal(new[] { "get_config", "get_status" }, transport.Names);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task FailedCaptureReportsErrorAndCannotExportPartialData()
    {
        var transport = new FakeTransport();
        var errors = new List<Exception>();
        await using var model = Create(() => transport, new TestHistoryStore(), errors);
        await ConnectTachModel(model, transport, true);
        var capture = model.CaptureTachAsync(60);
        await transport.NextAsync();
        TachDiagnosticsTests.Reply(transport, 10);
        await transport.NextAsync();
        TachDiagnosticsTests.Reply(transport, 20, dropped: true);
        await capture;
        Assert.False(model.TachCapturing);
        Assert.False(model.CanExportTach);
        Assert.False(model.Connected);
        Assert.IsType<InvalidDataException>(errors.Last());
        Assert.NotEmpty(model.Error);
    }

    [Fact]
    public async Task ShutdownCancelsAndAwaitsCaptureBeforeReleasingHistory()
    {
        var transport = new FakeTransport();
        var store = new TestHistoryStore();
        var model = Create(() => transport, store, new List<Exception>());
        await ConnectTachModel(model, transport, true);
        var capture = model.CaptureTachAsync(30);
        await transport.NextAsync();
        await model.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(capture.IsCompletedSuccessfully);
        Assert.True(transport.Disposed);
        Assert.True(store.Disposed);
        Assert.False(model.CanExportTach);
        Assert.False(model.TachCapturing);
    }

    [Fact]
    public async Task TachCaptureRequiresCapabilityAndExportsOnlyCompleteCaptures()
    {
        var transport = new FakeTransport();
        var errors = new List<Exception>();
        await using var model = Create(() => transport, new TestHistoryStore(), errors);
        await ConnectTachModel(model, transport, true);
        Assert.True(model.CanCaptureTach);
        Assert.False(model.CanExportTach);
        var capturing = model.CaptureTachAsync(30);
        Assert.True(model.TachCapturing);
        Assert.False(model.CanEdit);
        Assert.False(model.CanUpdateFirmware);
        Assert.False(model.CanExportTach);
        await TachDiagnosticsTests.CompleteCapture(transport, 30);
        await capturing;
        Assert.False(model.TachCapturing);
        Assert.True(model.CanEdit);
        Assert.True(model.CanExportTach);
        var path = Path.Combine(_directory, "tach.csv");
        await model.ExportTachAsync(path);
        Assert.Equal(121, File.ReadLines(path).Count());
        Assert.Empty(errors);
        var secondCapture = model.CaptureTachAsync(60);
        await transport.NextAsync();
        Assert.False(model.CanExportTach);
        model.CancelTachCapture();
        await secondCapture;
        Assert.False(model.CanExportTach);
    }

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
            startup ?? new(), scanners ?? (() => new Scanner()), errors.Add,
            new(Path.Combine(_directory, "device-names.json")));
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
    public async Task InvalidLocalFirmwareIsReportedWithoutInterruptingConnectionOrLosingDraft()
    {
        var transport = new FakeTransport { FirmwareUpdateSupported = true };
        var errors = new List<Exception>();
        await using var model = Create(() => transport, new(), errors);
        model.SelectedDevice = new(new(42, "Test", -50));
        var connecting = model.ConnectCommand.ExecuteAsync();
        await ControllerTestData.SynchronizeAsync(transport);
        await connecting;
        model.Profiles[0].Points[0].Duty = "35";
        var path = Path.Combine(_directory, "invalid.bin");
        await File.WriteAllBytesAsync(path, new byte[320]);
        Assert.True(model.CanUpdateFirmware);
        Assert.False(await model.UpdateFirmwareAsync(path, _ => Task.FromResult(true)));
        Assert.True(model.Connected);
        Assert.False(model.FirmwareUpdating);
        Assert.True(model.CanEdit);
        Assert.Equal("35", model.Profiles[0].Points[0].Duty);
        Assert.True(model.HasError);
        Assert.Single(errors);
        Assert.Equal(new[] { "get_config", "get_status" }, transport.Names);
    }

    [Fact]
    public async Task LaterVerifiedConnectionReplacesFailedFirmwareMessage()
    {
        var first = new FakeTransport { FirmwareUpdateSupported = true };
        var mismatched = new FakeTransport { FirmwareUpdateSupported = true };
        var recovered = new FakeTransport { FirmwareUpdateSupported = true };
        var transports = new Queue<FakeTransport>([first, mismatched, recovered]);
        var errors = new List<Exception>();
        await using var model = Create(() => transports.Dequeue(), new(), errors);
        model.SelectedDevice = new(new(42, "Test", -50));
        var connecting = model.ConnectCommand.ExecuteAsync();
        await ControllerTestData.SynchronizeAsync(first);
        await connecting;
        var path = Path.Combine(_directory, "recovery.bin");
        await File.WriteAllBytesAsync(path, FirmwareTests.ImageBytes());
        var image = await FirmwareImage.LoadAsync(path);
        var update = model.UpdateFirmwareAsync(path, _ => Task.FromResult(true));
        await FirmwareTests.BeginAsync(first, image);
        await FirmwareTests.ReplyAsync(first, "ota_finish", new
        {
            size = image.Bytes.Length,
            sha256 = image.FileSha256
        });
        await FirmwareTests.ReplyAsync(first, "ota_activate", new
        {
        });
        await ControllerTestData.SynchronizeAsync(mismatched);
        await FirmwareTests.ReplyAsync(mismatched, "ota_info", new
        {
            running_image_sha256 = image.FileSha256
        });
        Assert.False(await update);
        var failureMessage = model.FirmwareMessage;
        Assert.NotEmpty(errors);
        Assert.All(errors, error => Assert.IsType<InvalidDataException>(error));

        connecting = model.ConnectCommand.ExecuteAsync();
        await ControllerTestData.SynchronizeAsync(recovered);
        await FirmwareTests.ReplyAsync(recovered, "ota_info", new
        {
            running_image_sha256 = image.ImageSha256
        });
        await connecting;
        Assert.True(model.Connected);
        Assert.NotEqual(failureMessage, model.FirmwareMessage);
        Assert.False(model.HasError);
        Assert.Empty(recovered.FirmwareWrites);
        Assert.Single(first.Names, name => name == "ota_activate");
    }

    [Fact]
    public async Task ShutdownAwaitsFirmwareAndDisposesHistoryOnlyAfterTransport()
    {
        var transport = new FakeTransport { FirmwareUpdateSupported = true };
        var errors = new List<Exception>();
        var history = new TestHistoryStore();
        await using var model = Create(() => transport, history, errors);
        model.SelectedDevice = new(new(42, "Test", -50));
        var connecting = model.ConnectCommand.ExecuteAsync();
        await ControllerTestData.SynchronizeAsync(transport);
        await connecting;
        var path = Path.Combine(_directory, "firmware.bin");
        await File.WriteAllBytesAsync(path, FirmwareTests.ImageBytes());
        var update = model.UpdateFirmwareAsync(path, _ => Task.FromResult(true));
        Assert.Equal("ota_info", (await transport.NextAsync()).GetProperty("cmd").GetString());
        Assert.True(model.FirmwareUpdating);
        Assert.False(model.CanEdit);
        Assert.False(model.SaveCommand.CanExecute(null));
        Assert.False(model.ScanCommand.CanExecute(null));
        await model.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await update);
        Assert.True(transport.Disposed);
        Assert.True(history.Disposed);
        Assert.Empty(errors);
        Assert.DoesNotContain("ota_activate", transport.Names);
    }

    [Fact]
    public async Task CustomDeviceNamesPersistLocallyAndUpdateDashboardAndHistory()
    {
        var errors = new List<Exception>();
        await using var model = Create(() => new FakeTransport(), new(), errors);
        var sensor = model.Sensors[0];
        var fan = model.FanReadings[2];
        sensor.CustomName = "CPU";
        fan.CustomName = "Rear";

        Assert.Equal("CPU", sensor.Title);
        Assert.Equal("Rear", fan.Title);
        Assert.Equal("CPU • °C", model.History.Temperatures[0].Name);
        Assert.Equal("Rear • RPM", model.History.Rpm[2].Name);

        var saved = new DeviceNamePreferences(Path.Combine(_directory, "device-names.json")).Load();
        Assert.Equal("CPU", saved.Sensors[0]);
        Assert.Equal("Rear", saved.Fans[2]);
        Assert.Empty(errors);
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
