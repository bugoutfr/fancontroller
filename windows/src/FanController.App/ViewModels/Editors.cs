using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using FanController.Core;

namespace FanController.App.ViewModels;

public sealed class BleDeviceDisplay(BleCandidate candidate) : ObservableObject
{
    private BleCandidate _candidate = candidate;
    public ulong Address => _candidate.Address;
    public string Label => _candidate.Label;

    public void Update(BleCandidate candidate)
    {
        if (candidate.Address != Address)
            throw new ArgumentException("Une annonce ne peut pas changer l’adresse du périphérique.", nameof(candidate));
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
            throw new ArgumentException("Enter numeric temperature and PWM values.");
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
    public string Title => $"Temperature profile {Sensor} • fans {(Sensor == 1 ? "1 and 2" : "3 and 4")}";
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
                return [new("PWM (%) • linear interpolation", "#5DD9C1",
                    Read().Select(p => new PlotPoint(p.Temperature, p.Duty)).ToArray())];
            }
            catch (ArgumentException) { return []; }
        }
    }
    public ProfileEditor(int sensor, CurvePoint[] points)
    {
        Sensor = sensor;
        foreach (var p in points) Attach(new(p));
        AddCommand = new(() =>
        {
            var insertion = FindInsertion() ?? throw new InvalidOperationException("Aucun intervalle disponible pour un nouveau point.");
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
            throw new InvalidOperationException("Un profil doit conserver au moins deux points.");
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
        if (!IsEditing) throw new InvalidOperationException("Chart editing is disabled.");
        if (!double.IsFinite(temperature) || !double.IsFinite(duty))
            throw new ArgumentException("Les coordonnées du point doivent être finies.");
        var index = Points.IndexOf(point);
        if (index < 0) throw new ArgumentException("Ce point n’appartient pas au profil.", nameof(point));
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
    public string Title => $"Fan {Fan}";
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
    public FanSetting Read()
    {
        // En auto, le firmware conserve son ancien duty ; le champ n'est pas envoyé.
        var d = 0.0;
        if (ModeIndex == 1 && !double.TryParse(Duty, NumberStyles.Float, CultureInfo.CurrentCulture, out d))
            throw new ArgumentException("Fixed PWM must be a number from 0 to 100%.");
        var mode = ModeIndex switch { 0 => "auto", 1 => "fixed", _ => "" };
        Validation.Fan(Fan, mode, d);
        return new(mode, d);
    }
}

public sealed class SensorDisplay(string title) : ObservableObject
{
    private string _temperature = "En attente", _details = "Aucune donnée reçue.";
    private string _summary = "Aucune donnée reçue.";
    private bool _advanced;
    public bool Advanced { get => _advanced; set { if (Set(ref _advanced, value)) Changed(nameof(DisplayDetails)); } }
    public string DisplayDetails => Advanced ? Details : _summary;
    public string Title { get; } = title;
    public string Temperature { get => _temperature; private set => Set(ref _temperature, value); }
    public string Details { get => _details; private set => Set(ref _details, value); }

    public void Update(string temperature, string details, string? summary = null)
    {
        var oldDisplay = DisplayDetails;
        Temperature = temperature;
        Details = details;
        _summary = summary ?? details;
        if (oldDisplay != DisplayDetails) Changed(nameof(DisplayDetails));
    }
}

public sealed class FanDisplay(string title) : ObservableObject
{
    private string _rpm = "En attente", _details = "Aucune donnée reçue.";
    private string _summary = "Aucune donnée reçue.";
    private bool _advanced;
    public bool Advanced { get => _advanced; set { if (Set(ref _advanced, value)) Changed(nameof(DisplayDetails)); } }
    public string DisplayDetails => Advanced ? Details : _summary;
    public string Title { get; } = title;
    public string Rpm { get => _rpm; private set => Set(ref _rpm, value); }
    public string Details { get => _details; private set => Set(ref _details, value); }

    public void Update(string rpm, string details, string? summary = null)
    {
        var oldDisplay = DisplayDetails;
        Rpm = rpm;
        Details = details;
        _summary = summary ?? details;
        if (oldDisplay != DisplayDetails) Changed(nameof(DisplayDetails));
    }
}
