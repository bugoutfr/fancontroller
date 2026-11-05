using System.Text.Json;
using System.Text.Json.Serialization;

namespace FanController.Core;

/// <summary>A point on a linearly interpolated fan profile.</summary>
/// <param name="Temperature">Temperature in degrees Celsius.</param>
/// <param name="Duty">PWM duty percentage.</param>
public sealed record CurvePoint(double Temperature, double Duty);

/// <summary>The requested operating mode of a fan.</summary>
/// <param name="Mode">The wire mode, auto or fixed.</param>
/// <param name="Duty">Fixed PWM percentage; ignored when applying automatic mode.</param>
public sealed record FanSetting(string Mode, double Duty);

/// <summary>The versioned controller configuration read from or applied to remote RAM.</summary>
/// <param name="Version">The supported wire configuration version.</param>
/// <param name="Profiles">The two temperature profiles in sensor order.</param>
/// <param name="Fans">The four settings in fan order.</param>
public sealed record Configuration(int Version, CurvePoint[][] Profiles, FanSetting[] Fans);

/// <summary>A sensor reading and the firmware's safety state.</summary>
/// <param name="Sensor">One-based sensor number.</param>
/// <param name="Millivolts">Measured ADC voltage.</param>
/// <param name="Temperature">Temperature in degrees Celsius, or null on a sensor fault.</param>
/// <param name="Fault">The firmware fault code.</param>
/// <param name="Safety">Whether thermal safety is active.</param>
public sealed record SensorStatus(int Sensor, double Millivolts, double? Temperature, string Fault, bool Safety);

/// <summary>A fan reading, including requested and actually applied PWM.</summary>
/// <param name="Fan">One-based fan number.</param>
/// <param name="Sensor">The controlling sensor number.</param>
/// <param name="Mode">The operating mode, auto or fixed.</param>
/// <param name="FixedDuty">The stored fixed PWM percentage.</param>
/// <param name="TargetDuty">The regulated target PWM percentage.</param>
/// <param name="AppliedDuty">The actual PWM percentage.</param>
/// <param name="Rpm">Measured rotations per minute.</param>
/// <param name="PwmOk">Whether the PWM output is healthy.</param>
/// <param name="Boost">Whether the startup boost is active.</param>
public sealed record FanStatus(int Fan, int Sensor, string Mode, double FixedDuty, double TargetDuty,
    double AppliedDuty, double Rpm, bool PwmOk, bool Boost);

/// <summary>A complete status snapshot returned by the controller.</summary>
/// <param name="UptimeMs">Firmware uptime in milliseconds, with unsigned wraparound.</param>
/// <param name="Dirty">Whether remote RAM has unsaved changes.</param>
/// <param name="Storage">The remote persistence state code.</param>
/// <param name="StorageAvailable">Whether remote persistent storage is available.</param>
/// <param name="Sensors">The two sensor readings.</param>
/// <param name="Fans">The four fan readings.</param>
/// <param name="FirmwareVersion">The version, absent in older firmware.</param>
/// <param name="BleRssiDbm">RSSI received by the ESP32, or null if unsupported/unavailable.</param>
public sealed record Telemetry(uint UptimeMs, bool Dirty, string Storage, bool StorageAvailable,
    SensorStatus[] Sensors, FanStatus[] Fans, string? FirmwareVersion = null, int? BleRssiDbm = null);

/// <summary>Remote RAM configuration and its persistence state.</summary>
/// <param name="Configuration">The actual settings read from the controller.</param>
/// <param name="Dirty">Whether the settings have unsaved changes.</param>
/// <param name="Storage">The remote storage state code.</param>
public sealed record RemoteConfiguration(Configuration Configuration, bool Dirty, string Storage);

/// <summary>The shared firmware JSON contract, also used for stored samples.</summary>
public static class WireJson
{
    /// <summary>Snake-case options preserving fractional values and optional newer firmware fields.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    /// <summary>Deserializes a required wire value without fabricating a default on a null response.</summary>
    /// <typeparam name="T">The expected wire model.</typeparam>
    /// <param name="element">The JSON value.</param>
    /// <returns>The deserialized value.</returns>
    /// <exception cref="InvalidDataException">The JSON value is null.</exception>
    /// <exception cref="JsonException">The value does not match the expected contract.</exception>
    public static T Read<T>(JsonElement element) =>
        element.Deserialize<T>(Options) ?? throw new InvalidDataException("Réponse JSON vide.");
}

/// <summary>Enforces firmware constraints independently of presentation and localization.</summary>
public static class Validation
{
    /// <summary>The exception metadata key used by presentation to translate validation errors.</summary>
    public const string ResourceKey = "Validation.ResourceKey";

    /// <summary>Checks point count, finite bounds, temperature order and monotonic PWM.</summary>
    /// <exception cref="ArgumentException">The profile violates a firmware constraint.</exception>
    public static void Profile(IReadOnlyList<CurvePoint> points)
    {
        if (points.Count is < 2 or > 8)
            throw InvalidArgument("Validation.ProfilePointCount", "A profile must contain 2 to 8 points.");
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (!double.IsFinite(p.Temperature) || !double.IsFinite(p.Duty) || p.Temperature is < -20 or > 120 ||
                p.Duty is < 0 or > 100)
                throw InvalidArgument("Validation.ProfileLimits", "Temperature: −20 to 120 °C; PWM: 0 to 100%.");
            if (i > 0 && (p.Temperature <= points[i - 1].Temperature || p.Duty < points[i - 1].Duty))
                throw InvalidArgument("Validation.ProfileStrictlyIncreasing", "Strictly increasing temperatures and non-decreasing PWM required.");
        }
    }

    /// <summary>Checks the one-based fan number, mode and PWM bounds.</summary>
    /// <exception cref="ArgumentException">A setting is invalid.</exception>
    public static void Fan(int fan, string mode, double duty)
    {
        if (fan is < 1 or > 4 || mode is not ("auto" or "fixed") || !double.IsFinite(duty) || duty is < 0 or > 100)
            throw InvalidArgument("Validation.FanLimits", "Fan 1 to 4, mode auto/fixed and PWM 0 to 100 required.");
    }

    /// <summary>Checks the wire version, two profiles and four fan settings.</summary>
    /// <exception cref="InvalidDataException">The configuration version or dimensions are incompatible.</exception>
    /// <exception cref="ArgumentException">A profile or fan setting is invalid.</exception>
    public static void Config(Configuration config)
    {
        if (config.Version != 1 || config.Profiles is not { Length: 2 } || config.Fans is not { Length: 4 })
            throw InvalidData("Validation.IncompatibleFirmwareConfig", "Incompatible firmware configuration (version/dimensions).");
        foreach (var profile in config.Profiles)
            Profile(profile);
        for (var i = 0; i < 4; i++)
            Fan(i + 1, config.Fans[i].Mode, config.Fans[i].Duty);
    }

    /// <summary>Checks required sensor/fan identities, finite readings and supported state codes.</summary>
    /// <exception cref="InvalidDataException">Telemetry is incompatible or contains invalid values.</exception>
    public static void Status(Telemetry status)
    {
        if (status.Sensors is not { Length: 2 } || status.Fans is not { Length: 4 } ||
            !status.Sensors.Select(s => s.Sensor).Order().SequenceEqual(new[] { 1, 2 }) ||
            !status.Fans.Select(f => f.Fan).Order().SequenceEqual(new[] { 1, 2, 3, 4 }))
            throw InvalidData("Validation.IncompatibleFirmwareTelemetry", "Incompatible firmware telemetry.");
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
            throw InvalidData("Validation.InvalidTelemetryValues", "Invalid telemetry values.");
    }

    private static ArgumentException InvalidArgument(string key, string message)
    {
        var error = new ArgumentException(message);
        error.Data[ResourceKey] = key;
        return error;
    }

    private static InvalidDataException InvalidData(string key, string message)
    {
        var error = new InvalidDataException(message);
        error.Data[ResourceKey] = key;
        return error;
    }
}
