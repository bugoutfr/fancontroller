using System.Globalization;

namespace FanController.Core;

/// <summary>Persists only the last successfully synchronized Bluetooth address.</summary>
/// <param name="path">The address file; its parent is created when saving.</param>
public sealed class ConnectionPreferences(string path)
{
    /// <summary>Reads the stored address, or null when no preference has been saved.</summary>
    /// <exception cref="InvalidDataException">The file does not contain a 48-bit hexadecimal address.</exception>
    /// <exception cref="IOException">The preference cannot be read.</exception>
    public ulong? Load()
    {
        if (!File.Exists(path))
            return null;
        var text = File.ReadAllText(path).Trim();
        if (text.Length != 12 || !ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
            throw new InvalidDataException("Adresse du dernier contrôleur invalide.");
        return address;
    }

    /// <summary>Atomically replaces the stored address using a temporary sibling file.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The address exceeds 48 bits.</exception>
    /// <exception cref="IOException">The preference cannot be saved.</exception>
    public void Save(ulong address)
    {
        if (address > 0xFFFFFFFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(address));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, address.ToString("X12", CultureInfo.InvariantCulture));
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>Tracks explicit pause and capped exponential backoff for connection attempts.</summary>
public sealed class ReconnectPolicy
{
    private readonly object _sync = new();
    private int _failures;
    private bool _paused;

    /// <summary>Whether retries have been explicitly suspended.</summary>
    public bool Paused
    {
        get
        {
            lock (_sync)
                return _paused;
        }
    }

    /// <summary>The current delay, from two to thirty seconds.</summary>
    public TimeSpan Delay
    {
        get
        {
            lock (_sync)
                return TimeSpan.FromSeconds(Math.Min(30, 2 * (1 << Math.Min(_failures, 4))));
        }
    }

    /// <summary>Increases retry backoff up to its cap.</summary>
    public void Failed()
    {
        lock (_sync)
        {
            if (_failures < 4)
                _failures++;
        }
    }

    /// <summary>Resets backoff after a successful synchronization.</summary>
    public void Succeeded()
    {
        lock (_sync)
            _failures = 0;
    }

    /// <summary>Suspends automatic retries.</summary>
    public void Pause()
    {
        lock (_sync)
            _paused = true;
    }

    /// <summary>Enables retries and resets backoff after an explicit connection request.</summary>
    public void Resume()
    {
        lock (_sync)
        {
            _paused = false;
            _failures = 0;
        }
    }
}
