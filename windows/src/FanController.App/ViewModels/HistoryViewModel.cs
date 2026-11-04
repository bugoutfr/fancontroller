using System.Collections.ObjectModel;
using FanController.Core;
using Microsoft.UI.Dispatching;

namespace FanController.App.ViewModels;

public sealed record HistoryDevice(ulong Address)
{
    public string Label => $"Controller {Address:X12}";
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
    private string _message = "Historique local • conservation 30 jours";
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
            ex => { Diagnostics.Record(ex); Message = $"Historique indisponible : {ex.Message}"; });
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
            Message = $"Historique indisponible : {ex.Message}";
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
            Dispatch(() => { _writeError = $"History NOT saved: {ex.Message}"; Message = _writeError; });
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
            PlotSeries Series(int index, string name, string color)
            {
                var points = new List<PlotPoint>();
                foreach (var sample in samples)
                {
                    points.Add(new(sample.Timestamp.ToUnixTimeMilliseconds() / 1000.0, sample.Value(index),
                        $"{sample.SessionId}:{sample.Segment}"));
                }
                return new(name, color, points);
            }
            Temperatures = [Series(0, "Temperature 1 • °C", "#5DD9C1"), Series(1, "Temperature 2 • °C", "#FFB86B")];
            Rpm = [Series(2, "Fan 1 • RPM", "#5DD9C1"), Series(3, "Fan 2 • RPM", "#78A9FF"),
                Series(4, "Fan 3 • RPM", "#FFB86B"), Series(5, "Fan 4 • RPM", "#D99CFF")];
            Message = _writeError ?? "Readings received and saved on this PC • 30 days • gaps are not interpolated";
            _lastRefresh = now;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Record(ex);
            Message = $"Lecture de l’historique impossible : {ex.Message}";
        }
        finally
        {
            _reading = false;
            RefreshCommand.Refresh();
            if (range != RangeIndex || SelectedDevice != device)
                _dispatcher.TryEnqueue(() => RefreshCommand.Execute(null));
        }
    }
}
