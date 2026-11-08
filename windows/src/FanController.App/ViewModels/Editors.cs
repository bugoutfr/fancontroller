using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using FanController.Core;
using FanController.App.Localization;

namespace FanController.App.ViewModels;

/// <summary>A stable list item updated as advertisements arrive.</summary>
/// <param name="candidate">The initial advertisement.</param>
public sealed class BleDeviceDisplay(BleCandidate candidate) : ObservableObject
{
    private BleCandidate _candidate = candidate;

    /// <summary>The device address, which cannot change during an update.</summary>
    public ulong Address => _candidate.Address;

    /// <summary>The current scan label.</summary>
    public string Label => _candidate.Label;

    /// <summary>Updates the advertisement without replacing the selected list item.</summary>
    /// <exception cref="ArgumentException">The advertisement belongs to a different address.</exception>
    public void Update(BleCandidate candidate)
    {
        if (candidate.Address != Address)
            throw new ArgumentException("An advertisement cannot change the device address.", nameof(candidate));
        if (_candidate == candidate)
            return;
        _candidate = candidate;
        Changed(nameof(Label));
    }
}

/// <summary>A culture-aware text editor for one profile point.</summary>
public sealed class PointEditor : ObservableObject
{
    /// <summary>Deletes this point when the profile has more than two points.</summary>
    public ICommand? RemoveCommand
    {
        get; internal set;
    }

    private string _temperature, _duty;

    /// <summary>The editable temperature text.</summary>
    public string Temperature
    {
        get => _temperature; set => Set(ref _temperature, value);
    }

    /// <summary>The editable PWM percentage text.</summary>
    public string Duty
    {
        get => _duty; set => Set(ref _duty, value);
    }

    /// <summary>The localized delete action description.</summary>
    public string DeletePointToolTip => Localizer.Get("SettingsDeletePoint.ToolTipService.ToolTip", "Delete this point");

    /// <summary>Refreshes action descriptions after a language change.</summary>
    public void RefreshLocalization() => Changed(nameof(DeletePointToolTip));

    /// <summary>Formats a point using the active numeric culture.</summary>
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
    /// <summary>Parses both coordinates using the active numeric culture.</summary>
    /// <exception cref="ArgumentException">A coordinate is not numeric.</exception>
    public CurvePoint Read()
    {
        if (!double.TryParse(Temperature, NumberStyles.Float, CultureInfo.CurrentCulture, out var t) ||
            !double.TryParse(Duty, NumberStyles.Float, CultureInfo.CurrentCulture, out var d))
            throw new ArgumentException(Localizer.Get("Validation.Numeric", "Enter numeric temperature and PWM values."));
        return new(t, d);
    }
}

/// <summary>Edits a monotonic profile with the same points used by the chart and text fields.</summary>
public sealed class ProfileEditor : ObservableObject
{
    private sealed class DeletePointCommand(ProfileEditor profile, PointEditor point) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => profile.Points.Count > 2 && profile.Points.Contains(point);
        public void Execute(object? parameter)
        {
            if (CanExecute(parameter))
                profile.RemovePoint(point);
        }
        public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
    private bool _isEditing;
    private string? _sensorName, _firstFanName, _secondFanName;

    /// <summary>Whether chart dragging is allowed.</summary>
    public bool IsEditing
    {
        get => _isEditing; set => Set(ref _isEditing, value);
    }

    private PointEditor? _selectedPoint;

    /// <summary>The point selected for chart deletion.</summary>
    public PointEditor? SelectedPoint
    {
        get => _selectedPoint;
        set
        {
            if (!Set(ref _selectedPoint, value))
                return;
            Changed(nameof(HasSelectedPoint));
            Changed(nameof(Curve));
        }
    }

    /// <summary>Whether a point is selected.</summary>
    public bool HasSelectedPoint => SelectedPoint is not null;

    /// <summary>The one-based controlling sensor number.</summary>
    public int Sensor
    {
        get;
    }

    /// <summary>The localized profile heading.</summary>
    public string Title
    {
        get
        {
            var fans = Sensor == 1
                ? Localizer.Get("Profile.Fans1And2", "1 and 2")
                : Localizer.Get("Profile.Fans3And4", "3 and 4");
            var sensorName = string.IsNullOrWhiteSpace(_sensorName)
                ? Localizer.Format("Sensor.Title", "Probe {0}", Sensor) : _sensorName.Trim();
            if (!string.IsNullOrWhiteSpace(_firstFanName) || !string.IsNullOrWhiteSpace(_secondFanName))
            {
                var firstFan = string.IsNullOrWhiteSpace(_firstFanName)
                    ? Localizer.Format("Fan.Name", "Fan {0}", (Sensor - 1) * 2 + 1) : _firstFanName.Trim();
                var secondFan = string.IsNullOrWhiteSpace(_secondFanName)
                    ? Localizer.Format("Fan.Name", "Fan {0}", (Sensor - 1) * 2 + 2) : _secondFanName.Trim();
                fans = $"{firstFan}, {secondFan}";
            }
            return string.IsNullOrWhiteSpace(_sensorName) &&
                string.IsNullOrWhiteSpace(_firstFanName) && string.IsNullOrWhiteSpace(_secondFanName)
                ? Localizer.Format("Profile.Title", $"Probe profile {Sensor} • fans {fans}", Sensor, fans)
                : Localizer.Format("Profile.NamedTitle", "{0} • fans {1}", sensorName, fans);
        }
    }

    /// <summary>Updates the local names shown in this sensor's profile heading.</summary>
    public void SetDeviceNames(string? sensorName, string? firstFanName, string? secondFanName)
    {
        _sensorName = sensorName;
        _firstFanName = firstFanName;
        _secondFanName = secondFanName;
        Changed(nameof(Title));
    }

    /// <summary>The labels for coordinate text fields.</summary>
    public string PointLabelsText => Localizer.Get("SettingsPointLabels.Text", "Temperature (°C)                       PWM (%)");

    /// <summary>The localized delete action description.</summary>
    public string DeletePointToolTip => Localizer.Get("SettingsDeletePoint.ToolTipService.ToolTip", "Delete this point");

    /// <summary>The editable points in temperature order.</summary>
    public ObservableCollection<PointEditor> Points { get; } = [];

    /// <summary>Inserts a point in the widest interval, preserving the curve shape.</summary>
    public AsyncCommand AddCommand
    {
        get;
    }

    /// <summary>Removes the last point while retaining at least two points.</summary>
    public AsyncCommand RemoveCommand
    {
        get;
    }

    /// <summary>Reports a draft change to the owning settings presentation.</summary>
    public event Action? Edited;

    /// <summary>The validated linear chart series, or an empty series while input is invalid.</summary>
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

    /// <summary>Refreshes headings, chart labels and point action descriptions.</summary>
    public void RefreshLocalization()
    {
        Changed(nameof(Title));
        Changed(nameof(Curve));
        Changed(nameof(PointLabelsText));
        foreach (var point in Points)
            point.RefreshLocalization();
    }

    /// <summary>Creates a profile and its commands from existing points.</summary>
    /// <param name="sensor">The controlling sensor number.</param>
    /// <param name="points">The initial profile points.</param>
    /// <param name="onError">An optional UI error reporter; otherwise errors are recorded in diagnostics.</param>
    public ProfileEditor(int sensor, CurvePoint[] points, Action<Exception>? onError = null)
    {
        Sensor = sensor;
        foreach (var p in points)
            Attach(new(p));

        AddCommand = new(() =>
        {
            var insertion = FindInsertion() ?? throw new InvalidOperationException(Localizer.Get("Validation.NewPointUnavailable", "No interval is available for a new point."));
            var point = new PointEditor(insertion.Point);
            Attach(point, insertion.Index);
            SelectedPoint = point;
            NotifyEdit();
            return Task.CompletedTask;
        }, () => Points.Count < 8 && FindInsertion() is not null, onError ?? Diagnostics.Record);

        RemoveCommand = new(() =>
        {
            RemovePoint(Points[^1]);
            return Task.CompletedTask;
        }, () => Points.Count > 2, onError ?? Diagnostics.Record);
    }

    private void RemovePoint(PointEditor point)
    {
        if (Points.Count <= 2 || !Points.Contains(point))
            throw new InvalidOperationException(Localizer.Get("Validation.MinimumProfilePoints", "A profile must contain at least two points."));
        point.PropertyChanged -= PointChanged;
        Points.Remove(point);
        if (SelectedPoint == point)
            SelectedPoint = null;
        NotifyEdit();
    }

    private (int Index, CurvePoint Point)? FindInsertion()
    {
        CurvePoint[] values;
        try
        {
            values = Read();
        }
        catch (ArgumentException) { return null; }
        (int Index, CurvePoint Point)? result = null;
        var widest = 0.0;
        for (var i = 1; i < values.Length; i++)
        {
            var left = values[i - 1];
            var right = values[i];
            var gap = right.Temperature - left.Temperature;
            var temperature = left.Temperature + gap / 2;
            if (gap <= widest || temperature <= left.Temperature || temperature >= right.Temperature)
                continue;
            result = (i, new(temperature, left.Duty + (right.Duty - left.Duty) / 2));
            widest = gap;
        }
        return result;
    }

    /// <summary>Moves a chart point while clamping its coordinates to valid neighboring bounds.</summary>
    /// <exception cref="InvalidOperationException">Chart editing is disabled.</exception>
    /// <exception cref="ArgumentException">Coordinates are non-finite or the point belongs to another profile.</exception>
    public void MovePoint(PointEditor point, double temperature, double duty)
    {
        if (!IsEditing)
            throw new InvalidOperationException(Localizer.Get("Validation.ChartEditingDisabled", "Chart editing is disabled."));
        if (!double.IsFinite(temperature) || !double.IsFinite(duty))
            throw new ArgumentException(Localizer.Get("Validation.PointCoordinates", "Point coordinates must be finite numbers."));
        var index = Points.IndexOf(point);
        if (index < 0)
            throw new ArgumentException(Localizer.Get("Validation.PointNotInProfile", "This point does not belong to the profile."), nameof(point));
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
            if (point.RemoveCommand is DeletePointCommand command)
                command.Refresh();
        Changed(nameof(Curve));
        Edited?.Invoke();
    }

    /// <summary>Parses and validates the full profile before applying it.</summary>
    /// <exception cref="ArgumentException">A point or the profile ordering is invalid.</exception>
    public CurvePoint[] Read()
    {
        var points = Points.Select(p => p.Read()).ToArray();
        Validation.Profile(points);
        return points;
    }
}

/// <summary>A stable fan-mode choice whose label follows the selected UI language.</summary>
public sealed class FanModeOption : ObservableObject
{
    private readonly string _resourceKey;
    private readonly string _fallback;

    internal FanModeOption(string resourceKey, string fallback)
    {
        _resourceKey = resourceKey;
        _fallback = fallback;
    }

    /// <summary>The localized option label.</summary>
    public string Text => Localizer.Get(_resourceKey, _fallback);

    internal void RefreshLocalization() => Changed(nameof(Text));
}

/// <summary>Edits automatic or fixed fan control without validating inactive fixed-duty text.</summary>
public sealed class FanEditor : ObservableObject
{
    /// <summary>The one-based fan number.</summary>
    public int Fan
    {
        get;
    }

    /// <summary>The localized fan heading.</summary>
    public string Title => Localizer.Format("Fan.Name", $"Fan {Fan}", Fan);

    /// <summary>The automatic-mode option label.</summary>
    public string AutomaticText => ModeOptions[0].Text;

    /// <summary>The fixed-mode option label.</summary>
    public string FixedText => ModeOptions[1].Text;

    /// <summary>The localized mode options, ordered to match the selected index.</summary>
    public IReadOnlyList<FanModeOption> ModeOptions { get; } =
        [new("SettingsAutomatic.Content", "Automatic"), new("SettingsFixed.Content", "Fixed")];

    /// <summary>The fixed-duty input label.</summary>
    public string FixedModeText => Localizer.Get("SettingsFixedMode.Text", "% (fixed mode)");

    private int _modeIndex;
    private string _duty;

    /// <summary>The selected mode: zero for automatic, one for fixed.</summary>
    public int ModeIndex
    {
        get => _modeIndex; set => Set(ref _modeIndex, value);
    }

    /// <summary>The editable fixed PWM percentage.</summary>
    public string Duty
    {
        get => _duty; set => Set(ref _duty, value);
    }

    /// <summary>Creates editable fields from the current remote fan setting.</summary>
    public FanEditor(int fan, FanSetting setting)
    {
        Fan = fan;
        _modeIndex = setting.Mode == "auto" ? 0 : 1;
        _duty = setting.Duty.ToString(CultureInfo.CurrentCulture);
    }
    /// <summary>Refreshes headings and mode labels.</summary>
    public void RefreshLocalization()
    {
        Changed(nameof(Title));
        Changed(nameof(AutomaticText));
        Changed(nameof(FixedText));
        foreach (var option in ModeOptions)
            option.RefreshLocalization();
        Changed(nameof(FixedModeText));
    }
    /// <summary>Reads a validated setting, ignoring the PWM text in automatic mode.</summary>
    /// <exception cref="ArgumentException">The mode or active fixed-duty value is invalid.</exception>
    public FanSetting Read()
    {
        // En auto, le firmware conserve son ancien duty ; le champ n'est pas envoyé.
        var d = 0.0;
        if (ModeIndex == 1 && !double.TryParse(Duty, NumberStyles.Float, CultureInfo.CurrentCulture, out d))
            throw new ArgumentException(Localizer.Get("Validation.FixedPwm", "Fixed PWM must be a number from 0 to 100%"));
        var mode = ModeIndex switch
        {
            0 => "auto",
            1 => "fixed",
            _ => ""
        };
        Validation.Fan(Fan, mode, d);
        return new(mode, d);
    }
}

/// <summary>Shares waiting, summary/detail selection and change notifications for telemetry cards.</summary>
public abstract class TelemetryDisplay : ObservableObject
{
    private string _defaultTitle;

    /// <summary>The card heading.</summary>
    public string Title
    {
        get => string.IsNullOrWhiteSpace(CustomName) ? _defaultTitle : CustomName.Trim();
    }

    /// <summary>The localized numbered device reference, independent of its custom name.</summary>
    public abstract string ReferenceName
    {
        get;
    }

    private string _customName = "";

    /// <summary>The optional user-defined card name, stored only by the desktop client.</summary>
    public string CustomName
    {
        get => _customName;
        set
        {
            if (Set(ref _customName, value))
                Changed(nameof(Title));
        }
    }

    private string _reading, _details;
    private string _summary = "";
    private bool _advanced;
    private bool _hasReadings;

    /// <summary>Whether technical details replace the concise summary.</summary>
    public bool Advanced
    {
        get => _advanced; set
        {
            if (Set(ref _advanced, value))
                Changed(nameof(DisplayDetails));
        }
    }

    /// <summary>The selected summary or technical details.</summary>
    public string DisplayDetails => Advanced ? Details : _summary;

    /// <summary>The complete technical details.</summary>
    public string Details
    {
        get => _details; private set => Set(ref _details, value);
    }

    /// <summary>The current formatted reading.</summary>
    protected string Reading => _reading;

    /// <summary>The derived public reading property to notify.</summary>
    protected abstract string ReadingPropertyName
    {
        get;
    }

    /// <summary>The heading in the current language.</summary>
    protected abstract string LocalizedTitle
    {
        get;
    }

    /// <summary>Creates a card in its waiting state.</summary>
    protected TelemetryDisplay(string title)
    {
        _defaultTitle = title;
        _reading = Localizer.Get("State.Waiting", "Waiting");
        _details = Localizer.Get("State.NoData", "No readings received.");
        _summary = _details;
    }

    /// <summary>Refreshes the heading and waiting state without overwriting an existing reading.</summary>
    public void RefreshLocalization()
    {
        _defaultTitle = LocalizedTitle;
        Changed(nameof(Title));
        Changed(nameof(ReferenceName));
        if (!_hasReadings)
        {
            SetWaiting(Localizer.Get("State.Waiting", "Waiting"), Localizer.Get("State.NoData", "No readings received."));
        }
    }

    /// <summary>Updates a received reading without replacing the card or its collection item.</summary>
    protected void UpdateReading(string reading, string details, string? summary)
    {
        _hasReadings = true;
        var oldDisplay = DisplayDetails;
        Set(ref _reading, reading, ReadingPropertyName);
        Details = details;
        _summary = summary ?? details;
        if (oldDisplay != DisplayDetails)
            Changed(nameof(DisplayDetails));
    }
    /// <summary>Refreshes waiting labels only while the card has no received reading.</summary>
    public void SetWaiting(string waiting, string noData)
    {
        if (_hasReadings)
            return;
        var oldDisplay = DisplayDetails;
        Set(ref _reading, waiting, ReadingPropertyName);
        Details = noData;
        _summary = noData;
        if (oldDisplay != DisplayDetails)
            Changed(nameof(DisplayDetails));
    }
}

/// <summary>A sensor card retaining fault and safety information in its summary.</summary>
public sealed class SensorDisplay : TelemetryDisplay
{
    /// <summary>The one-based sensor number.</summary>
    public int Sensor
    {
        get;
    }

    /// <inheritdoc />
    public override string ReferenceName => Localizer.Format("Sensor.Title", "Probe {0}", Sensor);

    /// <summary>The formatted temperature, unavailable state or waiting label.</summary>
    public string Temperature => Reading;

    /// <inheritdoc />
    protected override string ReadingPropertyName => nameof(Temperature);

    /// <inheritdoc />
    protected override string LocalizedTitle => Localizer.Format("Sensor.Title", "Probe {0}", Sensor);

    /// <summary>Creates a sensor card for its identity and initial heading.</summary>
    public SensorDisplay(int sensor, string title) : base(title) => Sensor = sensor;

    /// <summary>Creates a card for the first sensor.</summary>
    public SensorDisplay(string title) : this(1, title) { }

    /// <summary>Updates the temperature, technical details and optional safety summary.</summary>
    public void Update(string temperature, string details, string? summary = null) =>
        UpdateReading(temperature, details, summary);
}

/// <summary>A fan card retaining its mode, applied PWM and output fault summary.</summary>
public sealed class FanDisplay : TelemetryDisplay
{
    /// <summary>The one-based fan number.</summary>
    public int Fan
    {
        get;
    }

    /// <inheritdoc />
    public override string ReferenceName => Localizer.Format("Fan.Name", "Fan {0}", Fan);

    /// <summary>The formatted RPM or waiting label.</summary>
    public string Rpm => Reading;

    /// <inheritdoc />
    protected override string ReadingPropertyName => nameof(Rpm);

    /// <inheritdoc />
    protected override string LocalizedTitle =>
        Localizer.Format("Fan.Title", "Fan {0} • probe {1}", Fan, (Fan - 1) / 2 + 1);

    /// <summary>Creates a fan card for its identity and initial heading.</summary>
    public FanDisplay(int fan, string title) : base(title) => Fan = fan;

    /// <summary>Creates a card for the first fan.</summary>
    public FanDisplay(string title) : this(1, title) { }

    /// <summary>Updates RPM, technical details and the optional concise summary.</summary>
    public void Update(string rpm, string details, string? summary = null) =>
        UpdateReading(rpm, details, summary);
}
