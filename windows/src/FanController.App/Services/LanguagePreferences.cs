namespace FanController.App.Services;

/// <summary>Persists the selected UI language without changing process culture or touching UI controls.</summary>
/// <param name="path">The language preference file.</param>
public sealed class LanguagePreferences(string path)
{
    /// <summary>Reads an English/French preference, or returns null on first launch.</summary>
    /// <exception cref="InvalidDataException">The saved language is unsupported.</exception>
    public string? Load()
    {
        if (!File.Exists(path))
            return null;
        var saved = File.ReadAllText(path).Trim();
        return saved is "en" or "fr"
            ? saved
            : throw new InvalidDataException("The saved language must be en or fr.");
    }

    /// <summary>Atomically saves a supported language; failures leave the previous preference intact.</summary>
    public void Save(string language)
    {
        if (language is not ("en" or "fr"))
            throw new ArgumentException("The language must be en or fr.", nameof(language));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, language);
        File.Move(temporary, path, overwrite: true);
    }
}
