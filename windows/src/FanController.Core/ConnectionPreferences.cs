using System.Globalization;

namespace FanController.Core;

public sealed class ConnectionPreferences(string path)
{
    public ulong? Load()
    {
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path).Trim();
        if (text.Length != 12 || !ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
            throw new InvalidDataException("Adresse du dernier contrôleur invalide.");
        return address;
    }

    public void Save(ulong address)
    {
        if (address > 0xFFFFFFFFFFFF) throw new ArgumentOutOfRangeException(nameof(address));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, address.ToString("X12", CultureInfo.InvariantCulture));
        File.Move(temporary, path, overwrite: true);
    }
}

public sealed class ReconnectPolicy
{
    private int _failures;
    public bool Paused { get; private set; }
    public TimeSpan Delay => TimeSpan.FromSeconds(Math.Min(30, 2 * (1 << Math.Min(_failures, 4))));
    public void Failed() { if (_failures < 4) _failures++; }
    public void Succeeded() => _failures = 0;
    public void Pause() => Paused = true;
    public void Resume() { Paused = false; _failures = 0; }
}
