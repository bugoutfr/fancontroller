using System.Collections.ObjectModel;
using System.Globalization;
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
    private string _temperature, _duty;
    public string Temperature { get => _temperature; set => Set(ref _temperature, value); }
    public string Duty { get => _duty; set => Set(ref _duty, value); }
    public PointEditor(CurvePoint point)
    {
        _temperature = point.Temperature.ToString(CultureInfo.CurrentCulture);
        _duty = point.Duty.ToString(CultureInfo.CurrentCulture);
    }
    public CurvePoint Read()
    {
        if (!double.TryParse(Temperature, NumberStyles.Float, CultureInfo.CurrentCulture, out var t) ||
            !double.TryParse(Duty, NumberStyles.Float, CultureInfo.CurrentCulture, out var d))
            throw new ArgumentException("Saisissez des valeurs numériques (virgule décimale) pour température et PWM.");
        return new(t, d);
    }
}

public sealed class ProfileEditor : ObservableObject
{
    public int Sensor { get; }
    public string Title => $"Profil de la sonde {Sensor} • ventilateurs {(Sensor == 1 ? "1 et 2" : "3 et 4")}";
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
                return [new("PWM (%) • interpolation linéaire", "#5DD9C1",
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
            Attach(new(new(0, 0)));
            NotifyEdit();
            return Task.CompletedTask;
        }, () => Points.Count < 8, _ => { });
        RemoveCommand = new(() =>
        {
            var last = Points[^1];
            last.PropertyChanged -= PointChanged;
            Points.RemoveAt(Points.Count - 1);
            NotifyEdit();
            return Task.CompletedTask;
        }, () => Points.Count > 2, _ => { });
    }
    private void Attach(PointEditor point)
    {
        point.PropertyChanged += PointChanged;
        Points.Add(point);
    }
    private void PointChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => NotifyEdit();
    private void NotifyEdit()
    {
        AddCommand.Refresh();
        RemoveCommand.Refresh();
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
    public string Title => $"Ventilateur {Fan}";
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
            throw new ArgumentException("Le PWM fixe doit être un nombre de 0 à 100 %.");
        var mode = ModeIndex switch { 0 => "auto", 1 => "fixed", _ => "" };
        Validation.Fan(Fan, mode, d);
        return new(mode, d);
    }
}

public sealed class SensorDisplay(string title) : ObservableObject
{
    private string _temperature = "En attente", _details = "Aucune mesure reçue.";
    private string _summary = "Aucune mesure reçue.";
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
    private string _rpm = "En attente", _details = "Aucune mesure reçue.";
    private string _summary = "Aucune mesure reçue.";
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
