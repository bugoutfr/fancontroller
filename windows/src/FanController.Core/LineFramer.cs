using System.Text;

namespace FanController.Core;

/// <summary>Accumule les octets, jamais les fragments de chaînes UTF-8.</summary>
public sealed class LineFramer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly List<byte> _bytes = [];
    public bool HasPartial => _bytes.Count != 0;
    public IReadOnlyList<string> Push(ReadOnlySpan<byte> bytes)
    {
        var lines = new List<string>();
        foreach (var b in bytes)
        {
            if (b == 0) throw new InvalidDataException("Octet NUL dans la réponse.");
            if (b == 10)
            {
                if (_bytes.Count > 0 && _bytes[^1] == 13) _bytes.RemoveAt(_bytes.Count - 1);
                if (_bytes.Count == 0) throw new InvalidDataException("Réponse vide.");
                lines.Add(StrictUtf8.GetString(_bytes.ToArray()));
                _bytes.Clear();
            }
            else
            {
                if (_bytes.Count >= 32768) throw new InvalidDataException("Réponse BLE trop longue.");
                _bytes.Add(b);
            }
        }
        return lines;
    }
}
