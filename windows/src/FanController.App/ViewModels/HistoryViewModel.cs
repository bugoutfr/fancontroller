using System.Collections.ObjectModel;
using FanController.Core;
using FanController.App.Localization;
using FanController.App.Services;

namespace FanController.App.ViewModels;

/// <summary>A selectable controller with locally stored measurements.</summary>
/// <param name="address">The controller Bluetooth address.</param>
public sealed class HistoryDevice(ulong address) : ObservableObject
{
    /// <summary>The Bluetooth address.</summary>
    public ulong Address { get; } = address;

    /// <summary>The localized controller label.</summary>
    public string Label => Localizer.Format("History.Controller", "Controller {0:X12}", Address);

    /// <summary>Refreshes the label after a language change.</summary>
    public void RefreshLocalization() => Changed(nameof(Label));
}

/// <summary>Presents local history; owns the injected store until all reads have stopped.</summary>
public sealed class HistoryViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IUiDispatcher _dispatcher;
    private readonly IHistoryStore _store;
    private readonly Action<Exception> _log;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _initialization, _shutdown;
    private bool _disposed;
    private HistoryDevice? _selectedDevice;
    private DateTimeOffset _lastRefresh;
    private int _rangeIndex;
    private bool _reading;
    private string? _writeError;
    private string _message = Localizer.Get("History.InitialMessage", "Local history • 30-day retention");
    private string? _readError;
    private IReadOnlyList<HistorySample> _lastSamples = [];
    private IReadOnlyList<PlotSeries> _temperatures = [], _rpm = [];
    /// <summary>The controllers available for selection.</summary>
    public ObservableCollection<HistoryDevice> Devices { get; } = [];

    /// <summary>Temperature series retaining missing measurements and disconnected segments.</summary>
    public IReadOnlyList<PlotSeries> Temperatures
    {
        get => _temperatures; private set => Set(ref _temperatures, value);
    }

    /// <summary>The four fan speed series.</summary>
    public IReadOnlyList<PlotSeries> Rpm
    {
        get => _rpm; private set => Set(ref _rpm, value);
    }

    /// <summary>The current history status or disk error.</summary>
    public string Message
    {
        get => _message; private set => Set(ref _message, value);
    }

    /// <summary>The controller whose stored measurements are displayed.</summary>
    public HistoryDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!Set(ref _selectedDevice, value))
                return;
            RefreshCommand.Refresh();
            RefreshCommand.Execute(null);
        }
    }
    /// <summary>The selected range: hour, day, week or thirty days.</summary>
    public int RangeIndex
    {
        get => _rangeIndex;
        set
        {
            if (Set(ref _rangeIndex, value))
                RefreshCommand.Execute(null);
        }
    }
    /// <summary>Reloads the selected controller and range without overlapping reads.</summary>
    public AsyncCommand RefreshCommand
    {
        get;
    }

    /// <summary>Creates history presentation using an injected dispatcher and local store.</summary>
    public HistoryViewModel(IUiDispatcher dispatcher, IHistoryStore store, Action<Exception> log)
    {
        _dispatcher = dispatcher;
        _store = store;
        _log = log;
        RefreshCommand = new(RefreshAsync, () => !_disposed && !_reading && SelectedDevice is not null, ex =>
        {
            if (ex is OperationCanceledException && _disposed)
                return;
            ReportReadError(ex);
        });
    }

    /// <summary>Prunes expired files, discovers stored controllers and selects the first when available.</summary>
    public Task InitializeAsync() => _initialization ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        try
        {
            await Task.Run(() => _store.PruneAsync(DateTimeOffset.UtcNow, _lifetime.Token), _lifetime.Token);
            var addresses = await Task.Run(_store.Devices, _lifetime.Token);
            if (_disposed)
                return;
            foreach (var address in addresses)
                if (!Devices.Any(d => d.Address == address))
                    Devices.Add(new(address));
            SelectedDevice ??= Devices.FirstOrDefault();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportReadError(ex);
        }
    }

    /// <summary>Adds a controller when needed and selects its history.</summary>
    public void SelectDevice(ulong address)
    {
        var device = Devices.FirstOrDefault(d => d.Address == address);
        if (device is null)
        {
            device = new(address);
            Devices.Add(device);
        }
        SelectedDevice = device;
        _lastRefresh = default;
    }

    /// <summary>Updates history availability after acquisition has saved a sample.</summary>
    public void MeasurementWritten(ulong address) => _dispatcher.Post(() =>
    {
        if (_disposed)
            return;
        _writeError = null;
        if (SelectedDevice?.Address == address && DateTimeOffset.UtcNow - _lastRefresh >= TimeSpan.FromSeconds(10))
            RefreshCommand.Execute(null);
    });

    /// <summary>Displays a disk write failure without stopping controller acquisition.</summary>
    public void ReportWriteError(Exception error)
    {
        _log(error);
        _dispatcher.Post(() =>
        {
            if (_disposed)
                return;
            _writeError = error.Message;
            Message = Localizer.Format("History.WriteError", "History NOT saved: {0}", error.Message);
        });
    }

    private void ReportReadError(Exception error)
    {
        _log(error);
        _readError = error.Message;
        Message = Localizer.Format("History.ReadError", "Unable to read history: {0}", error.Message);
    }

    private async Task RefreshAsync()
    {
        _reading = true;
        RefreshCommand.Refresh();
        try
        {
            while (!_disposed && SelectedDevice is { } device)
            {
                var range = RangeIndex;
                var now = DateTimeOffset.UtcNow;
                var duration = range switch
                {
                    0 => TimeSpan.FromHours(1),
                    1 => TimeSpan.FromHours(24),
                    2 => TimeSpan.FromDays(7),
                    _ => TimeSpan.FromDays(30)
                };

                var samples = await Task.Run(async () =>
                {
                    await _store.PruneAsync(now, _lifetime.Token);
                    return await _store.ReadAsync(device.Address, now - duration, now, _lifetime.Token);
                }, _lifetime.Token);
                if (_disposed)
                    return;
                if (SelectedDevice != device || RangeIndex != range)
                    continue;

                _lastSamples = samples;
                _readError = null;
                RebuildSeries();
                RefreshMessage();
                _lastRefresh = now;
                return;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportReadError(ex);
        }
        finally
        {
            _reading = false;
            RefreshCommand.Refresh();
        }
    }

    /// <summary>Refreshes labels and series names without rereading disk.</summary>
    public void RefreshLocalization()
    {
        foreach (var device in Devices)
            device.RefreshLocalization();
        RebuildSeries();
        RefreshMessage();
    }

    private void RefreshMessage()
    {
        Message = _writeError is not null
            ? Localizer.Format("History.WriteError", "History NOT saved: {0}", _writeError)
            : _readError is not null
                ? Localizer.Format("History.ReadError", "Unable to read history: {0}", _readError)
                : SelectedDevice is null
                    ? Localizer.Get("History.InitialMessage", "Local history • 30-day retention")
                    : Localizer.Get("History.RecordedMessage", "Readings received and saved on this PC • 30 days • gaps are not interpolated");
    }

    /// <summary>Cancels reads and waits for their termination before disposing the owned store.</summary>
    public ValueTask DisposeAsync() => new(_shutdown ??= ShutdownAsync());

    private async Task ShutdownAsync()
    {
        _disposed = true;
        _lifetime.Cancel();
        RefreshCommand.Refresh();
        await Task.WhenAll(RefreshCommand.ExecutionTask, _initialization ?? Task.CompletedTask);
        if (_store is IDisposable disposable)
            disposable.Dispose();
        _lifetime.Dispose();
    }

    private void RebuildSeries()
    {
        PlotSeries Series(int index, string name, string color)
        {
            var points = _lastSamples.Select(sample => new PlotPoint(
                sample.Timestamp.ToUnixTimeMilliseconds() / 1000.0, sample.Value(index),
                $"{sample.SessionId}:{sample.Segment}")).ToArray();
            return new(name, color, points);
        }
        Temperatures = [
            Series(0, Localizer.Format("History.ProbeSeries", "Probe {0} • °C", 1), "#5DD9C1"),
            Series(1, Localizer.Format("History.ProbeSeries", "Probe {0} • °C", 2), "#FFB86B")];
        Rpm = [
            Series(2, Localizer.Format("History.FanSeries", "Fan {0} • RPM", 1), "#5DD9C1"),
            Series(3, Localizer.Format("History.FanSeries", "Fan {0} • RPM", 2), "#78A9FF"),
            Series(4, Localizer.Format("History.FanSeries", "Fan {0} • RPM", 3), "#FFB86B"),
            Series(5, Localizer.Format("History.FanSeries", "Fan {0} • RPM", 4), "#D99CFF")];
    }
}
