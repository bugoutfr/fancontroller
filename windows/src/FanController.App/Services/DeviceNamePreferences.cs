using System.Text.Json;

namespace FanController.App.Services;

/// <summary>Contains the optional local labels for the controller's fixed sensors and fans.</summary>
public sealed record DeviceNames(string[] Sensors, string[] Fans)
{
    /// <summary>Creates an empty set of custom labels.</summary>
    public static DeviceNames Empty => new(["", ""], ["", "", "", ""]);
}

/// <summary>Stores sensor and fan labels on this PC, separately from controller configuration.</summary>
/// <param name="path">The local preference file.</param>
public sealed class DeviceNamePreferences(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Reads saved labels or returns empty labels on first launch.</summary>
    /// <exception cref="InvalidDataException">The file contains unsupported label data.</exception>
    /// <exception cref="IOException">The preference file cannot be read.</exception>
    public DeviceNames Load()
    {
        if (!File.Exists(path))
            return DeviceNames.Empty;

        var names = JsonSerializer.Deserialize<DeviceNames>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Les noms locaux des ventilateurs et sondes sont invalides.");
        Validate(names, fromFile: true);
        return names;
    }

    /// <summary>Atomically saves labels locally without sending them to the controller.</summary>
    /// <exception cref="ArgumentException">The labels do not match the supported devices.</exception>
    /// <exception cref="IOException">The preference file cannot be saved.</exception>
    public void Save(DeviceNames names)
    {
        Validate(names, fromFile: false);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(names, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private static void Validate(DeviceNames names, bool fromFile)
    {
        var valid = names.Sensors is { Length: 2 } &&
            names.Fans is { Length: 4 } &&
            names.Sensors.Concat(names.Fans).All(name =>
                name is not null && name.Length <= 40 && !name.Any(char.IsControl));
        if (valid)
            return;

        if (fromFile)
            throw new InvalidDataException("Les noms locaux doivent contenir deux sondes et quatre ventilateurs, avec 40 caractères maximum.");
        throw new ArgumentException("Les noms doivent contenir deux sondes et quatre ventilateurs, avec 40 caractères maximum.", nameof(names));
    }
}
