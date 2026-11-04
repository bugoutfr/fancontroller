using System.Collections.ObjectModel;
using FanController.Core;
using FanController.App.Localization;
using Microsoft.UI.Dispatching;

namespace FanController.App.ViewModels;

public sealed class HistoryDevice(ulong address) : ObservableObject
{
    public ulong Address { get; } = address;
    public string Label => Localizer.Format("History.Controller", "Controller {0:X12}", Address);
    public void RefreshLocalization() => Changed(nameof(Label));
}

public sealed class HistoryViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcher;
    private readonly HistoryStore _store = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanController", "history"));
    private HistoryDevice? _selectedDevice;
    private DateTimeOffset _lastRefresh;
    private int _rangeIndex;
    private bool _reading;
    private string? _writeError;
    private string _message = Localizer.Get("History.InitialMessage", "Local history • 30-day retention");
    private string? _readError;
    private IReadOnlyList<HistorySample> _lastSamples = [];
    private IReadOnlyList<PlotSeries> _temperatures = [], _rpm = [];
    public ObservableCollection<HistoryDevice> Devices { get; } = [];
    public IReadOnlyList<PlotSeries> Temperatures { get => _temperatures; private set => Set(ref _temperatures, value); }
    public IReadOnlyList<PlotSeries> Rpm { get => _rpm; private set => Set(ref _rpm, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public HistoryDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!Set(ref _selectedDevice, value)) return;
            RefreshCommand.Refresh();
            RefreshCommand.Execute(null);
        }
    }
    public int RangeIndex
    {
        get => _rangeIndex;
        set { if (Set(ref _rangeIndex, value)) RefreshCommand.Execute(null); }
    }
    public AsyncCommand RefreshCommand { get; }

    public HistoryViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        RefreshCommand = new(RefreshAsync, () => !_reading && SelectedDevice is not null,
            ex => { Diagnostics.Record(ex); _readError = ex.Message; Message = Localizer.Format("History.ReadError", "Unable to read history: {0}", ex.Message); });
    }

    private void Dispatch(Action action)
    {
        if (_dispatcher.HasThreadAccess) action(); else _dispatcher.TryEnqueue(() => action());
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _store.PruneAsync(DateTimeOffset.UtcNow);
            foreach (var address in await Task.Run(_store.Devices))
                if (!Devices.Any(d => d.Address == address)) Devices.Add(new(address));
            SelectedDevice ??= Devices.FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Record(ex);
            _readError = ex.Message;
            Message = Localizer.Format("History.ReadError", "Unable to read history: {0}", ex.Message);
        }
    }

    public void SelectDevice(ulong address)
    {
        var device = Devices.FirstOrDefault(d => d.Address == address);
        if (device is null) { device = new(address); Devices.Add(device); }
        SelectedDevice = device;
        _lastRefresh = default;
    }

    public async Task RecordAsync(Telemetry status, ulong address, string session, CancellationToken ct)
    {
        try
        {
            var sample = HistorySample.FromTelemetry(DateTimeOffset.UtcNow, status) with { SessionId = session };
            await _store.AppendAsync(address, sample, ct).ConfigureAwait(false);
            Dispatch(() =>
            {
                _writeError = null;
                if (SelectedDevice?.Address == address && DateTimeOffset.UtcNow - _lastRefresh >= TimeSpan.FromSeconds(10))
                    RefreshCommand.Execute(null);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Record(ex);
            Dispatch(() =>
            {
                _writeError = ex.Message;
                Message = Localizer.Format("History.WriteError", "History NOT saved: {0}", ex.Message);
            });
        }
    }

    private async Task RefreshAsync()
    {
        if (SelectedDevice is not { } device) return;
        _reading = true;
        RefreshCommand.Refresh();
        var range = RangeIndex;
        try
        {
            var now = DateTimeOffset.UtcNow;
            await _store.PruneAsync(now);
            var duration = range switch
            {
                0 => TimeSpan.FromHours(1),
                1 => TimeSpan.FromHours(24),
                2 => TimeSpan.FromDays(7),
                _ => TimeSpan.FromDays(30)
            };
            var samples = await Task.Run(() => _store.ReadAsync(device.Address, now - duration, now));
            if (SelectedDevice != device) return;
            _lastSamples = samples;
            _readError = null;
            RebuildSeries();
            Message = _writeError is null
                ? Localizer.Get("History.RecordedMessage", "Readings received and saved on this PC • 30 days • gaps are not interpolated")
                : Localizer.Format("History.WriteError", "History NOT saved: {0}", _writeError);
            _lastRefresh = now;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Record(ex);
            _readError = ex.Message;
            Message = Localizer.Format("History.ReadError", "Unable to read history: {0}", ex.Message);
        }
        finally
        {
            _reading = false;
            RefreshCommand.Refresh();
            if (range != RangeIndex || SelectedDevice != device)
                _dispatcher.TryEnqueue(() => RefreshCommand.Execute(null));
        }
    }

    public void RefreshLocalization()
    {
        foreach (var device in Devices) device.RefreshLocalization();
        RebuildSeries();
        Message = _writeError is not null
            ? Localizer.Format("History.WriteError", "History NOT saved: {0}", _writeError)
            : _readError is not null
                ? Localizer.Format("History.ReadError", "Unable to read history: {0}", _readError)
                : SelectedDevice is null
                    ? Localizer.Get("History.InitialMessage", "Local history • 30-day retention")
                    : Localizer.Get("History.RecordedMessage", "Readings received and saved on this PC • 30 days • gaps are not interpolated");
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
