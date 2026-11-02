using System.Text.Json;
using System.Text.Json.Serialization;

namespace FanController.Core;

public sealed record CurvePoint(double Temperature, double Duty);
public sealed record FanSetting(string Mode, double Duty);
public sealed record Configuration(int Version, CurvePoint[][] Profiles, FanSetting[] Fans);
public sealed record SensorStatus(int Sensor, double Millivolts, double? Temperature, string Fault, bool Safety);
public sealed record FanStatus(int Fan, int Sensor, string Mode, double FixedDuty, double TargetDuty,
    double AppliedDuty, double Rpm, bool PwmOk, bool Boost);
public sealed record Telemetry(uint UptimeMs, bool Dirty, string Storage, bool StorageAvailable,
    SensorStatus[] Sensors, FanStatus[] Fans, string? FirmwareVersion = null, int? BleRssiDbm = null);
public sealed record RemoteConfiguration(Configuration Configuration, bool Dirty, string Storage);

public static class WireJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    public static T Read<T>(JsonElement element) =>
        element.Deserialize<T>(Options) ?? throw new InvalidDataException("Réponse JSON vide.");
}

public static class Validation
{
    public static void Profile(IReadOnlyList<CurvePoint> points)
    {
        if (points.Count is < 2 or > 8)
            throw new ArgumentException("Un profil doit contenir de 2 à 8 points.");
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (!double.IsFinite(p.Temperature) || !double.IsFinite(p.Duty) || p.Temperature is < -20 or > 120 ||
                p.Duty is < 0 or > 100)
                throw new ArgumentException("Température : −20 à 120 °C ; PWM : 0 à 100 %.");
            if (i > 0 && (p.Temperature <= points[i - 1].Temperature || p.Duty < points[i - 1].Duty))
                throw new ArgumentException("Températures strictement croissantes et PWM non décroissants requis.");
        }
    }

    public static void Fan(int fan, string mode, double duty)
    {
        if (fan is < 1 or > 4 || mode is not ("auto" or "fixed") || !double.IsFinite(duty) || duty is < 0 or > 100)
            throw new ArgumentException("Ventilateur 1 à 4, mode auto/fixed et PWM 0 à 100 requis.");
    }

    public static void Config(Configuration config)
    {
        if (config.Version != 1 || config.Profiles is not { Length: 2 } || config.Fans is not { Length: 4 })
            throw new InvalidDataException("Configuration firmware incompatible (version/dimensions).");
        foreach (var profile in config.Profiles) Profile(profile);
        for (var i = 0; i < 4; i++) Fan(i + 1, config.Fans[i].Mode, config.Fans[i].Duty);
    }

    public static void Status(Telemetry status)
    {
        if (status.Sensors is not { Length: 2 } || status.Fans is not { Length: 4 } ||
            !status.Sensors.Select(s => s.Sensor).Order().SequenceEqual(new[] { 1, 2 }) ||
            !status.Fans.Select(f => f.Fan).Order().SequenceEqual(new[] { 1, 2, 3, 4 }))
            throw new InvalidDataException("Télémétrie firmware incompatible.");
        if (status.BleRssiDbm is < -127 or > 20 ||
            string.IsNullOrEmpty(status.Storage) ||
            status.Sensors.Any(s => s.Temperature is { } t && !double.IsFinite(t)) ||
            status.Sensors.Any(s => !double.IsFinite(s.Millivolts) || s.Millivolts < 0) ||
            status.Sensors.Any(s => s.Fault is not ("none" or "short_circuit" or "open_circuit" or "out_of_range")) ||
            status.Fans.Any(f => f.Sensor is < 1 or > 2 || f.Mode is not ("auto" or "fixed") ||
                                !double.IsFinite(f.FixedDuty) || !double.IsFinite(f.TargetDuty) ||
                                !double.IsFinite(f.AppliedDuty) || !double.IsFinite(f.Rpm) || f.Rpm < 0 ||
                                f.FixedDuty is < 0 or > 100 || f.TargetDuty is < 0 or > 100 ||
                                f.AppliedDuty is < 0 or > 100))
            throw new InvalidDataException("Valeurs de télémétrie invalides.");
    }
}
