using System.Text;

namespace FanController.Core;

/// <summary>Accumule les octets, jamais les fragments de chaînes UTF-8.</summary>
public sealed class LineFramer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly List<byte> _bytes = [];

    /// <summary>Indique si des octets attendent encore un terminateur de ligne.</summary>
    public bool HasPartial => _bytes.Count != 0;

    /// <summary>Accumule un fragment et décode les lignes UTF-8 terminées par LF ou CRLF.</summary>
    /// <param name="bytes">Octets reçus, pouvant contenir plusieurs lignes ou une ligne partielle.</param>
    /// <returns>Les lignes complètes sans leur terminateur, dans l'ordre reçu.</returns>
    /// <exception cref="InvalidDataException">Une ligne est vide, dépasse 32 768 octets ou contient NUL.</exception>
    /// <exception cref="DecoderFallbackException">Une ligne complète contient un encodage UTF-8 invalide.</exception>
    public IReadOnlyList<string> Push(ReadOnlySpan<byte> bytes)
    {
        var lines = new List<string>();
        foreach (var b in bytes)
        {
            if (b == 0)
                throw new InvalidDataException("Octet NUL dans la réponse.");
            if (b == 10)
            {
                if (_bytes.Count > 0 && _bytes[^1] == 13)
                    _bytes.RemoveAt(_bytes.Count - 1);
                if (_bytes.Count == 0)
                    throw new InvalidDataException("Réponse vide.");
                lines.Add(StrictUtf8.GetString(_bytes.ToArray()));
                _bytes.Clear();
            }
            else
            {
                if (_bytes.Count >= 32768)
                    throw new InvalidDataException("Réponse BLE trop longue.");
                _bytes.Add(b);
            }
        }

        return lines;
    }
}
