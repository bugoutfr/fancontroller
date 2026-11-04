using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using FanController.Core;
using FanController.App.Localization;

namespace FanController.App.ViewModels;

public sealed class BleDeviceDisplay(BleCandidate candidate) : ObservableObject
{
    private BleCandidate _candidate = candidate;
    public ulong Address => _candidate.Address;
    public string Label => _candidate.Label;

    public void Update(BleCandidate candidate)
    {
        if (candidate.Address != Address)
            throw new ArgumentException("An advertisement cannot change the device address.", nameof(candidate));
        if (_candidate == candidate) return;
        _candidate = candidate;
        Changed(nameof(Label));
    }
}

public sealed class PointEditor : ObservableObject
{
    public ICommand? RemoveCommand { get; internal set; }
    private string _temperature, _duty;
    public string Temperature { get => _temperature; set => Set(ref _temperature, value); }
    public string Duty { get => _duty; set => Set(ref _duty, value); }
    public string DeletePointToolTip => Localizer.Get("SettingsDeletePoint.ToolTipService.ToolTip", "Delete this point");
    public void RefreshLocalization() => Changed(nameof(DeletePointToolTip));
    public PointEditor(CurvePoint point)
    {
        _temperature = point.Temperature.ToString(CultureInfo.CurrentCulture);
        _duty = point.Duty.ToString(CultureInfo.CurrentCulture);
    }
    internal void Move(CurvePoint point)
    {
        _temperature = point.Temperature.ToString(CultureInfo.CurrentCulture);
        _duty = point.Duty.ToString(CultureInfo.CurrentCulture);
        Changed(nameof(Temperature));
        Changed(nameof(Duty));
    }
    public CurvePoint Read()
    {
        if (!double.TryParse(Temperature, NumberStyles.Float, CultureInfo.CurrentCulture, out var t) ||
            !double.TryParse(Duty, NumberStyles.Float, CultureInfo.CurrentCulture, out var d))
            throw new ArgumentException(Localizer.Get("Validation.Numeric", "Enter numeric temperature and PWM values."));
        return new(t, d);
    }
}

public sealed class ProfileEditor : ObservableObject
{
    private sealed class DeletePointCommand(ProfileEditor profile, PointEditor point) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => profile.Points.Count > 2 && profile.Points.Contains(point);
        public void Execute(object? parameter)
        {
            if (CanExecute(parameter)) profile.RemovePoint(point);
        }
        public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
    private bool _isEditing;
    public bool IsEditing { get => _isEditing; set => Set(ref _isEditing, value); }
    private PointEditor? _selectedPoint;
    public PointEditor? SelectedPoint
    {
        get => _selectedPoint;
        set
        {
            if (!Set(ref _selectedPoint, value)) return;
            Changed(nameof(HasSelectedPoint));
            Changed(nameof(Curve));
        }
    }
    public bool HasSelectedPoint => SelectedPoint is not null;
    public int Sensor { get; }
    public string Title
    {
        get
        {
            var fans = Sensor == 1
                ? Localizer.Get("Profile.Fans1And2", "1 and 2")
                : Localizer.Get("Profile.Fans3And4", "3 and 4");
            return Localizer.Format("Profile.Title", $"Probe profile {Sensor} • fans {fans}", Sensor, fans);
        }
    }
    public string PointLabelsText => Localizer.Get("SettingsPointLabels.Text", "Temperature (°C)                       PWM (%)");
    public string DeletePointToolTip => Localizer.Get("SettingsDeletePoint.ToolTipService.ToolTip", "Delete this point");
    public ObservableCollection<PointEditor> Points { get; } = [];
    public AsyncCommand AddCommand { get; }
    public AsyncCommand RemoveCommand { get; }
    public event Action? Edited;
    public IReadOnlyList<PlotSeries> Curve
    {
        get
        {
            try
            {
                return [new(Localizer.Get("Plot.PwmLabel", "PWM (%) • linear interpolation"), "#5DD9C1",
                    Read().Select(p => new PlotPoint(p.Temperature, p.Duty)).ToArray())];
            }
            catch (ArgumentException) { return []; }
        }
    }
    public void RefreshLocalization()
    {
        Changed(nameof(Title));
        Changed(nameof(Curve));
        Changed(nameof(PointLabelsText));
        foreach (var point in Points) point.RefreshLocalization();
    }
    public ProfileEditor(int sensor, CurvePoint[] points)
    {
        Sensor = sensor;
        foreach (var p in points) Attach(new(p));
        AddCommand = new(() =>
        {
            var insertion = FindInsertion() ?? throw new InvalidOperationException(Localizer.Get("Validation.NewPointUnavailable", "No interval is available for a new point."));
            var point = new PointEditor(insertion.Point);
            Attach(point, insertion.Index);
            SelectedPoint = point;
            NotifyEdit();
            return Task.CompletedTask;
        }, () => Points.Count < 8 && FindInsertion() is not null, _ => { });
        RemoveCommand = new(() =>
        {
            RemovePoint(Points[^1]);
            return Task.CompletedTask;
        }, () => Points.Count > 2, _ => { });
    }
    private void RemovePoint(PointEditor point)
    {
        if (Points.Count <= 2 || !Points.Contains(point))
            throw new InvalidOperationException(Localizer.Get("Validation.MinimumProfilePoints", "A profile must contain at least two points."));
        point.PropertyChanged -= PointChanged;
        Points.Remove(point);
        if (SelectedPoint == point) SelectedPoint = null;
        NotifyEdit();
    }
    private (int Index, CurvePoint Point)? FindInsertion()
    {
        CurvePoint[] values;
        try { values = Read(); }
        catch (ArgumentException) { return null; }
        (int Index, CurvePoint Point)? result = null;
        var widest = 0.0;
        for (var i = 1; i < values.Length; i++)
        {
            var left = values[i - 1];
            var right = values[i];
            var gap = right.Temperature - left.Temperature;
            var temperature = left.Temperature + gap / 2;
            if (gap <= widest || temperature <= left.Temperature || temperature >= right.Temperature) continue;
            result = (i, new(temperature, left.Duty + (right.Duty - left.Duty) / 2));
            widest = gap;
        }
        return result;
    }
    public void MovePoint(PointEditor point, double temperature, double duty)
    {
        if (!IsEditing) throw new InvalidOperationException(Localizer.Get("Validation.ChartEditingDisabled", "Chart editing is disabled."));
        if (!double.IsFinite(temperature) || !double.IsFinite(duty))
            throw new ArgumentException(Localizer.Get("Validation.PointCoordinates", "Point coordinates must be finite numbers."));
        var index = Points.IndexOf(point);
        if (index < 0) throw new ArgumentException(Localizer.Get("Validation.PointNotInProfile", "This point does not belong to the profile."), nameof(point));
        var values = Read();
        var current = values[index];
        var minTemperature = index == 0 ? -20 : Math.Min(current.Temperature, values[index - 1].Temperature + 0.1);
        var maxTemperature = index == values.Length - 1 ? 120 : Math.Max(current.Temperature, values[index + 1].Temperature - 0.1);
        var minDuty = index == 0 ? 0 : values[index - 1].Duty;
        var maxDuty = index == values.Length - 1 ? 100 : values[index + 1].Duty;
        point.Move(new(Math.Clamp(Math.Round(temperature, 1), minTemperature, maxTemperature),
            Math.Clamp(Math.Round(duty, 1), minDuty, maxDuty)));
    }
    private void Attach(PointEditor point, int? index = null)
    {
        point.RemoveCommand = new DeletePointCommand(this, point);
        point.PropertyChanged += PointChanged;
        Points.Insert(index ?? Points.Count, point);
    }
    private void PointChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => NotifyEdit();
    private void NotifyEdit()
    {
        AddCommand.Refresh();
        RemoveCommand.Refresh();
        foreach (var point in Points)
            if (point.RemoveCommand is DeletePointCommand command) command.Refresh();
        Changed(nameof(Curve));
        Edited?.Invoke();
    }
    public CurvePoint[] Read()
    {
        var points = Points.Select(p => p.Read()).ToArray();
        Validation.Profile(points);
        return points;
    }
}

public sealed class FanEditor : ObservableObject
{
    public int Fan { get; }
    public string Title => Localizer.Format("Fan.Name", $"Fan {Fan}", Fan);
    public string AutomaticText => Localizer.Get("SettingsAutomatic.Content", "Automatic");
    public string FixedText => Localizer.Get("SettingsFixed.Content", "Fixed");
    public string FixedModeText => Localizer.Get("SettingsFixedMode.Text", "% (fixed mode)");
    private int _modeIndex;
    private string _duty;
    public int ModeIndex { get => _modeIndex; set => Set(ref _modeIndex, value); }
    public string Duty { get => _duty; set => Set(ref _duty, value); }
    public FanEditor(int fan, FanSetting setting)
    {
        Fan = fan;
        _modeIndex = setting.Mode == "auto" ? 0 : 1;
        _duty = setting.Duty.ToString(CultureInfo.CurrentCulture);
    }
    public void RefreshLocalization()
    {
        Changed(nameof(Title));
        Changed(nameof(AutomaticText));
        Changed(nameof(FixedText));
        Changed(nameof(FixedModeText));
    }
    public FanSetting Read()
    {
        // En auto, le firmware conserve son ancien duty ; le champ n'est pas envoyé.
        var d = 0.0;
        if (ModeIndex == 1 && !double.TryParse(Duty, NumberStyles.Float, CultureInfo.CurrentCulture, out d))
            throw new ArgumentException(Localizer.Get("Validation.FixedPwm", "Fixed PWM must be a number from 0 to 100%"));
        var mode = ModeIndex switch { 0 => "auto", 1 => "fixed", _ => "" };
        Validation.Fan(Fan, mode, d);
        return new(mode, d);
    }
}

public sealed class SensorDisplay : ObservableObject
{
    public int Sensor { get; }
    private string _title;
    public string Title { get => _title; private set => Set(ref _title, value); }
    private string _temperature = "", _details = "";
    private string _summary = "";
    private bool _advanced;
    private bool _hasReadings;
    public bool Advanced { get => _advanced; set { if (Set(ref _advanced, value)) Changed(nameof(DisplayDetails)); } }
    public string DisplayDetails => Advanced ? Details : _summary;
    public string Temperature { get => _temperature; private set => Set(ref _temperature, value); }
    public string Details { get => _details; private set => Set(ref _details, value); }

    public SensorDisplay(int sensor, string title)
    {
        Sensor = sensor;
        _title = title;
        _temperature = Localizer.Get("State.Waiting", "Waiting");
        _details = Localizer.Get("State.NoData", "No readings received.");
        _summary = _details;
    }

    public SensorDisplay(string title) : this(1, title) { }

    public void RefreshLocalization()
    {
        Title = Localizer.Format("Sensor.Title", "Probe {0}", Sensor);
        if (!_hasReadings)
        {
            SetWaiting(Localizer.Get("State.Waiting", "Waiting"), Localizer.Get("State.NoData", "No readings received."));
        }
    }

    public void Update(string temperature, string details, string? summary = null)
    {
        _hasReadings = true;
        var oldDisplay = DisplayDetails;
        Temperature = temperature;
        Details = details;
        _summary = summary ?? details;
        if (oldDisplay != DisplayDetails) Changed(nameof(DisplayDetails));
    }
    public void SetWaiting(string waiting, string noData)
    {
        if (_hasReadings) return;
        var oldDisplay = DisplayDetails;
        Temperature = waiting;
        Details = noData;
        _summary = noData;
        if (oldDisplay != DisplayDetails) Changed(nameof(DisplayDetails));
    }
}

public sealed class FanDisplay : ObservableObject
{
    public int Fan { get; }
    private string _title;
    public string Title { get => _title; private set => Set(ref _title, value); }
    private string _rpm = "", _details = "";
    private string _summary = "";
    private bool _advanced;
    private bool _hasReadings;
    public bool Advanced { get => _advanced; set { if (Set(ref _advanced, value)) Changed(nameof(DisplayDetails)); } }
    public string DisplayDetails => Advanced ? Details : _summary;
    public string Rpm { get => _rpm; private set => Set(ref _rpm, value); }
    public string Details { get => _details; private set => Set(ref _details, value); }

    public FanDisplay(int fan, string title)
    {
        Fan = fan;
        _title = title;
        _rpm = Localizer.Get("State.Waiting", "Waiting");
        _details = Localizer.Get("State.NoData", "No readings received.");
        _summary = _details;
    }

    public FanDisplay(string title) : this(1, title) { }

    public void RefreshLocalization()
    {
        Title = Localizer.Format("Fan.Title", "Fan {0} • probe {1}", Fan, (Fan - 1) / 2 + 1);
        if (!_hasReadings)
        {
            SetWaiting(Localizer.Get("State.Waiting", "Waiting"), Localizer.Get("State.NoData", "No readings received."));
        }
    }

    public void Update(string rpm, string details, string? summary = null)
    {
        _hasReadings = true;
        var oldDisplay = DisplayDetails;
        Rpm = rpm;
        Details = details;
        _summary = summary ?? details;
        if (oldDisplay != DisplayDetails) Changed(nameof(DisplayDetails));
    }
    public void SetWaiting(string waiting, string noData)
    {
        if (_hasReadings) return;
        var oldDisplay = DisplayDetails;
        Rpm = waiting;
        Details = noData;
        _summary = noData;
        if (oldDisplay != DisplayDetails) Changed(nameof(DisplayDetails));
    }
}
